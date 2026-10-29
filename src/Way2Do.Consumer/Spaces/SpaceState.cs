namespace Way2Do.Consumer.Spaces;

/// <summary>Resultado de um evento recebido, como aparece em "Últimos eventos".</summary>
public enum IngestResult
{
    /// <summary>Estado da tarefa gravado (versão mais nova que a guardada).</summary>
    Applied,
    /// <summary>eventId já processado: descartado sem efeito.</summary>
    Duplicate,
    /// <summary>Versão igual ou mais antiga que a guardada: fora de ordem ou desatualizado, descartado.</summary>
    Stale,
    /// <summary>webhook.ping: só confirma o recebimento.</summary>
    Ping,
    /// <summary>Tipo desconhecido: aceito e ignorado (compatibilidade com tipos novos do contrato).</summary>
    Ignored,
    /// <summary>Tarefa nova num espaço já no limite de tarefas: descartada.</summary>
    LimitReached,
}

public enum FailureMode { Off, Down, Flaky }

/// <summary>Tudo o que o painel mostra (GET /v/&lt;H&gt;/state).</summary>
public sealed record SpaceState(
    DateTimeOffset CreatedAt,
    IReadOnlyList<TodoView> Todos,
    CounterView Counters,
    HealthView Health,
    FailureModeView FailureMode,
    IReadOnlyList<RecentEventView> RecentEvents);

public sealed record TodoView(
    Guid Id,
    Guid? UserId,
    string UserName,
    long Version,
    string Title,
    string? Description,
    DateOnly? DueDate,
    string Priority,
    string Status,
    IReadOnlyList<string> Tags,
    bool Urgent,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DeletedAt,
    bool Deleted,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Contadores sobre as tarefas não excluídas (as excluídas só entram em <see cref="Deleted"/>). "Vencidas" fica com
/// a página: depende da data local do aparelho, como no app (docs/escopo.md, seção 7).
/// </summary>
public sealed record CounterView(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByPriority,
    int UrgentPending,
    int Deleted,
    IReadOnlyDictionary<string, int> ByUser);

public sealed record HealthView(
    long Received,
    IReadOnlyDictionary<string, long> EventsByType,
    long Duplicates,
    long Stale,
    long SnapshotsRequested,
    long DroppedByLimit,
    double? LatencyAvgMs,
    double? LatencyMaxMs,
    DateTimeOffset? LastEventAt);

public sealed record FailureModeView(string Mode, long Refused);

public sealed record RecentEventView(
    DateTimeOffset ReceivedAt,
    Guid EventId,
    string Type,
    Guid? TodoId,
    string? Title,
    long? Version,
    string Result);
