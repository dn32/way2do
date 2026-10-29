namespace Way2Do.Consumer.Spaces;

/// <summary>
/// Projeção, em memória, dos eventos recebidos por um espaço (identificado por <c>H = SHA-256(T)</c>). Várias
/// requisições chegam ao mesmo tempo (entregas do worker, o painel lendo o estado); um lock por espaço serializa
/// tudo o que toca o estado dele, sem um espaço esperar pelo outro.
/// </summary>
public sealed class Space(string id, DateTimeOffset createdAt, SpaceLimits limits)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TodoView> _todos = [];
    private readonly HashSet<Guid> _seenIds = [];
    private readonly Queue<Guid> _seenOrder = new();
    private readonly Dictionary<string, long> _eventsByType = [];
    private readonly Queue<RecentEventView> _recent = new();

    private long _received;
    private long _duplicates;
    private long _stale;
    private long _snapshotsRequested;
    private long _droppedByLimit;
    private long _refused;
    private double _latencySumMs;
    private double _latencyMaxMs;
    private DateTimeOffset? _lastEventAt;
    private FailureMode _failureMode = FailureMode.Off;

    public string Id { get; } = id;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>
    /// Modo "fora do ar" (recurso de demonstração): a entrada é recusada com 503 antes de ser processada, como um
    /// consumidor que não responde. Conta a recusa.
    /// </summary>
    public bool RefuseBeforeProcessing()
    {
        lock (_gate)
        {
            if (_failureMode != FailureMode.Down)
                return false;
            _refused++;
            return true;
        }
    }

    /// <summary>
    /// Modo "instável" (recurso de demonstração): o evento JÁ foi processado, mas em ~50% das vezes a confirmação se
    /// "perde" e a resposta é 503. O Way2Do reenvia, e o reenvio aparece no painel como duplicado descartado: é a
    /// entrega at-least-once e a deduplicação por eventId acontecendo ao vivo. Conta a recusa.
    /// </summary>
    public bool DropAcknowledgement()
    {
        lock (_gate)
        {
            if (_failureMode != FailureMode.Flaky || Random.Shared.Next(2) != 0)
                return false;
            _refused++;
            return true;
        }
    }

    public void SetFailureMode(FailureMode mode)
    {
        lock (_gate)
            _failureMode = mode;
    }

    /// <summary>
    /// Aplica um evento já validado. <paramref name="snapshotRequested"/> indica que a resposta será 205 (o espaço
    /// acabou de nascer): só conta o pedido de retrato; quem escolhe o código é o endpoint.
    /// </summary>
    public IngestResult Apply(WebhookEvent evt, DateTimeOffset receivedAt, bool snapshotRequested)
    {
        var eventId = evt.EventId!.Value;
        var type = evt.Type!;

        lock (_gate)
        {
            _received++;
            _lastEventAt = receivedAt;
            var typeKey = EventTypes.IsKnown(type) ? type : EventTypes.Other;
            _eventsByType[typeKey] = _eventsByType.GetValueOrDefault(typeKey) + 1;

            // Latência inclui as reentregas: com o modo de falha ligado, mostra o atraso do backoff. Relógios
            // diferentes podem dar valor negativo; conta como zero.
            var latencyMs = Math.Max(0, (receivedAt - evt.OccurredAt!.Value).TotalMilliseconds);
            _latencySumMs += latencyMs;
            _latencyMaxMs = Math.Max(_latencyMaxMs, latencyMs);

            if (snapshotRequested)
                _snapshotsRequested++;

            IngestResult result;
            if (!_seenIds.Add(eventId))
            {
                _duplicates++;
                result = IngestResult.Duplicate;
            }
            else
            {
                Remember(eventId);
                result = type switch
                {
                    EventTypes.Ping => IngestResult.Ping,
                    _ when EventTypes.IsTodoEvent(type) => ApplyTodo(evt, receivedAt),
                    _ => IngestResult.Ignored,
                };
            }

            _recent.Enqueue(new RecentEventView(receivedAt, eventId, typeKey, evt.Todo?.Id,
                evt.Todo?.Title is { } title ? Clip(title, SpaceLimits.MaxTitle) : null, evt.Todo?.Version,
                ResultCode(result)));
            while (_recent.Count > SpaceLimits.RecentEvents)
                _recent.Dequeue();

            return result;
        }
    }

    public SpaceState ToState()
    {
        lock (_gate)
        {
            var todos = _todos.Values
                .OrderBy(t => t.Deleted)
                .ThenByDescending(t => t.UpdatedAt)
                .ToList();
            var active = todos.Where(t => !t.Deleted).ToList();

            var counters = new CounterView(
                Total: active.Count,
                ByStatus: CountBy(active, t => t.Status),
                ByPriority: CountBy(active, t => t.Priority),
                UrgentPending: active.Count(t => t.Urgent && t.Status == "pending"),
                Deleted: todos.Count - active.Count,
                ByUser: CountBy(active, t => t.UserName));

            var health = new HealthView(
                Received: _received,
                EventsByType: new Dictionary<string, long>(_eventsByType),
                Duplicates: _duplicates,
                Stale: _stale,
                SnapshotsRequested: _snapshotsRequested,
                DroppedByLimit: _droppedByLimit,
                LatencyAvgMs: _received == 0 ? null : Math.Round(_latencySumMs / _received, 1),
                LatencyMaxMs: _received == 0 ? null : Math.Round(_latencyMaxMs, 1),
                LastEventAt: _lastEventAt);

            return new SpaceState(
                CreatedAt,
                todos,
                counters,
                health,
                new FailureModeView(ModeCode(_failureMode), _refused),
                _recent.Reverse().ToList());
        }
    }

    // Regra de versão: só aplica o estado se for mais novo que o guardado. Assim um evento atrasado por retry, ou o
    // retrato completo correndo em paralelo com alterações novas, nunca sobrescreve o que já é mais recente.
    private IngestResult ApplyTodo(WebhookEvent evt, DateTimeOffset receivedAt)
    {
        var todo = evt.Todo!;
        var id = todo.Id!.Value;
        var version = todo.Version!.Value;

        if (_todos.TryGetValue(id, out var current))
        {
            if (version <= current.Version)
            {
                _stale++;
                return IngestResult.Stale;
            }
        }
        else if (_todos.Count >= limits.MaxTodosPerSpace)
        {
            _droppedByLimit++;
            return IngestResult.LimitReached;
        }

        var deleted = evt.Type == EventTypes.Deleted || todo.DeletedAt is not null;
        _todos[id] = new TodoView(
            id,
            todo.UserId,
            Clip(todo.UserName ?? "", SpaceLimits.MaxShortText),
            version,
            Clip(todo.Title ?? "", SpaceLimits.MaxTitle),
            todo.Description is null ? null : Clip(todo.Description, SpaceLimits.MaxDescription),
            todo.DueDate,
            Clip(todo.Priority ?? "", SpaceLimits.MaxShortText),
            Clip(todo.Status ?? "", SpaceLimits.MaxShortText),
            (todo.Tags ?? []).Take(SpaceLimits.MaxTags).Select(t => Clip(t ?? "", SpaceLimits.MaxTag)).ToList(),
            todo.Urgent,
            todo.CreatedAt,
            todo.DeletedAt ?? (deleted ? evt.OccurredAt : null),
            deleted,
            evt.OccurredAt!.Value,
            receivedAt);
        return IngestResult.Applied;
    }

    private void Remember(Guid eventId)
    {
        _seenOrder.Enqueue(eventId);
        while (_seenOrder.Count > limits.MaxSeenEventIds)
            _seenIds.Remove(_seenOrder.Dequeue());
    }

    private static Dictionary<string, int> CountBy(IEnumerable<TodoView> todos, Func<TodoView, string> key) =>
        todos.GroupBy(key).ToDictionary(g => g.Key, g => g.Count());

    // Corta sem partir um par substituto (emoji etc.), que viraria UTF-16 inválido no JSON.
    private static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut];
    }

    public static string ResultCode(IngestResult result) => result switch
    {
        IngestResult.Applied => "applied",
        IngestResult.Duplicate => "duplicate",
        IngestResult.Stale => "stale",
        IngestResult.Ping => "ping",
        IngestResult.Ignored => "ignored",
        _ => "limit",
    };

    public static string ModeCode(FailureMode mode) => mode switch
    {
        FailureMode.Down => "down",
        FailureMode.Flaky => "flaky",
        _ => "off",
    };

    public static FailureMode? ParseMode(string? code) => code switch
    {
        "off" => FailureMode.Off,
        "down" => FailureMode.Down,
        "flaky" => FailureMode.Flaky,
        _ => null,
    };
}
