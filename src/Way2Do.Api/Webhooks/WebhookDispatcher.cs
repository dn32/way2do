using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Way2Do.Api.Data;

namespace Way2Do.Api.Webhooks;

/// <summary>Acorda o worker logo depois de um commit com eventos novos (latência de milissegundos).</summary>
public sealed class WebhookSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // já sinalizado: o worker vai acordar de qualquer jeito
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Entrega os eventos do outbox (escopo, seção 6). Cada rodada enfileira os retratos completos agendados e entrega um
/// lote de pendências: reservado com <c>FOR UPDATE SKIP LOCKED</c> (duas instâncias nunca pegam a mesma entrega),
/// enviado em ordem de <c>changeSeq</c> dentro de cada assinatura e em paralelo entre assinaturas. Cada assinatura
/// envia por conta própria: um consumidor lento ou fora do ar não segura a rodada dos outros. A ordem é boa prática,
/// não garantia (uma entrega em backoff pode ser ultrapassada por uma nova); quem garante é a versão no evento.
/// </summary>
public sealed class WebhookDispatcher(IServiceScopeFactory scopes, WebhookHttp http, WebhookSignal signal, TimeProvider clock,
    ILogger<WebhookDispatcher> logger)
{
    public const int BatchSize = 50;

    /// <summary>Reserva de uma entrega em andamento; se a instância cair no meio, outra assume depois disso.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);

    /// <summary>No máximo um retrato completo por assinatura neste intervalo.</summary>
    public static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(1);

    // Assinaturas com envio em andamento: ficam fora da próxima reserva até terminar (a ordem dentro delas se mantém).
    private readonly ConcurrentDictionary<Guid, Task> _sending = new();

    /// <summary>
    /// Uma rodada; devolve quantas entregas foram reservadas. O worker não espera os envios
    /// (<paramref name="waitForSends"/> falso): cada assinatura segue sozinha e acorda o worker ao terminar. Os testes
    /// esperam, para conferir o resultado.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct, bool waitForSends = true)
    {
        await EnqueueDueSnapshotsAsync(ct);
        var claimed = await ClaimAsync(_sending.Keys.ToArray(), ct);
        var sends = new List<Task>();
        foreach (var group in claimed.GroupBy(c => c.SubscriptionId))
        {
            var subscriptionId = group.Key;
            var send = SendGroupAsync(group.ToList(), ct);
            _sending[subscriptionId] = send;
            sends.Add(send.ContinueWith(_ =>
            {
                _sending.TryRemove(subscriptionId, out Task? _);
                signal.Notify(); // pode haver mais desta assinatura esperando
            }, TaskScheduler.Default));
        }
        if (waitForSends)
            await Task.WhenAll(sends);
        return claimed.Count;
    }

    /// <summary>Próximo horário em que há algo a fazer (entrega ou retrato), para o worker não dormir demais.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var delivery = await db.WebhookDeliveries
            .Where(d => d.Status == DeliveryStatus.Pending)
            .MinAsync(d => (DateTimeOffset?)d.NextAttemptAt, ct);
        var resync = await db.WebhookSubscriptions.MinAsync(s => s.ResyncDueAt, ct);
        return delivery is null ? resync : resync is null ? delivery : delivery < resync ? delivery : resync;
    }

    /// <summary>
    /// Pedido de retrato completo (o consumidor respondeu 205). Fica agendado para agora ou, se já houve um no último
    /// minuto, para o fim do intervalo; pedidos repetidos se juntam no mesmo horário.
    /// </summary>
    public static void RequestResync(WebhookSubscription subscription, DateTimeOffset now)
    {
        if (subscription.ResyncDueAt is not null)
            return;
        var earliest = subscription.LastResyncAt + ResyncInterval;
        subscription.ResyncDueAt = earliest > now ? earliest : now;
    }

    /// <summary>Espera antes da próxima tentativa: 1 s, 2 s, 4 s… até 5 min, com jitter (entre a metade e o total).</summary>
    public static TimeSpan Backoff(int attempts, double random)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Clamp(attempts - 1, 0, 30)));
        return TimeSpan.FromSeconds(seconds / 2 + random * seconds / 2);
    }

    private async Task EnqueueDueSnapshotsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        // Consulta barata antes de abrir a transação: quase sempre não há retrato vencido.
        if (!await db.WebhookSubscriptions.AnyAsync(s => s.ResyncDueAt <= now, ct))
            return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var due = await db.WebhookSubscriptions
            .FromSql($"SELECT * FROM webhook_subscriptions WHERE resync_due_at <= {now} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
        foreach (var subscription in due)
        {
            var count = await OutboxWriter.AddSnapshotAsync(db, subscription, now, ct);
            subscription.LastResyncAt = now;
            subscription.ResyncDueAt = null;
            logger.LogInformation("Retrato completo para {Url}: {Count} tarefas.", WebhookUrl.Mask(subscription.Url), count);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private sealed record Claimed(Guid DeliveryId, Guid SubscriptionId, string Url, Guid EventId, string Type, string Payload,
        int Attempts, DateTimeOffset CreatedAt);

    private async Task<List<Claimed>> ClaimAsync(Guid[] busySubscriptions, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var leaseUntil = now + Lease;

        // Reserva atômica: empurra o horário da tentativa para o fim da reserva. Ordem do change_seq, por boa prática
        // (o consumidor já tolera desordem pela versão).
        var ids = await db.Database.SqlQuery<Guid>($"""
            WITH due AS (
                SELECT d.id
                FROM webhook_deliveries d
                JOIN outbox_events e ON e.id = d.event_id
                WHERE d.status = 'pending' AND d.next_attempt_at <= {now}
                  AND NOT (d.subscription_id = ANY({busySubscriptions}))
                ORDER BY e.change_seq NULLS FIRST, e.occurred_at, d.created_at
                LIMIT {BatchSize}
                FOR UPDATE OF d SKIP LOCKED)
            UPDATE webhook_deliveries d SET next_attempt_at = {leaseUntil}
            FROM due WHERE d.id = due.id
            RETURNING d.id AS "Value"
            """).ToListAsync(ct);
        if (ids.Count == 0)
            return [];

        var rows = await (
            from d in db.WebhookDeliveries
            join e in db.OutboxEvents on d.EventId equals e.Id
            join s in db.WebhookSubscriptions on d.SubscriptionId equals s.Id
            where ids.Contains(d.Id)
            select new { d, e, s.Url }).ToListAsync(ct);
        return rows
            .OrderBy(r => r.e.ChangeSeq ?? long.MinValue).ThenBy(r => r.e.OccurredAt).ThenBy(r => r.d.CreatedAt)
            .Select(r => new Claimed(r.d.Id, r.d.SubscriptionId, r.Url, r.e.Id, r.e.Type, r.e.Payload, r.d.Attempts, r.d.CreatedAt))
            .ToList();
    }

    private async Task SendGroupAsync(List<Claimed> deliveries, CancellationToken ct)
    {
        try
        {
            await SendInOrderAsync(deliveries, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // As entregas não gravadas continuam reservadas e voltam depois da reserva (5 min).
            logger.LogError(e, "Falha ao entregar para {Url}.", WebhookUrl.Mask(deliveries[0].Url));
        }
    }

    private async Task SendInOrderAsync(List<Claimed> deliveries, CancellationToken ct)
    {
        for (var i = 0; i < deliveries.Count; i++)
        {
            var delivery = deliveries[i];
            var response = await http.PostAsync(delivery.Url, delivery.EventId, delivery.Type, delivery.Payload,
                WebhookHttp.DeliveryTimeout, ct);
            var (proceed, retryAt) = await RecordAsync(delivery, response, ct);
            if (retryAt is not null)
            {
                // Consumidor com problema: as demais deste lote voltam para a fila junto com a que falhou, sem contar
                // tentativa (senão cada uma esperaria o timeout de 10 s, em série).
                await ReleaseAsync(deliveries.Skip(i + 1).Select(d => d.DeliveryId).ToList(), retryAt.Value, ct);
                return;
            }
            if (!proceed)
                return; // assinatura removida no meio do envio: nada mais vai para essa URL
        }
    }

    /// <summary>
    /// Grava o resultado. <c>Proceed</c> falso: a assinatura não existe mais. <c>RetryAt</c>: a entrega falhou (ou
    /// foi desistida) e as demais do lote esperam até esse horário.
    /// </summary>
    private async Task<(bool Proceed, DateTimeOffset? RetryAt)> RecordAsync(Claimed claimed, WebhookResponse response,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == claimed.DeliveryId, ct);
        if (delivery is null)
            return (false, null); // assinatura removida: as entregas dela foram canceladas (cascata)

        delivery.Attempts++;
        DateTimeOffset? retryAt = null;
        if (response.Delivered)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = now;
            delivery.LastError = null;
            // 205 pede o retrato completo, inclusive vindo de um evento do retrato (consumidor que reiniciou no meio
            // dele). O limite de um retrato por minuto, com pedidos agrupados, impede o laço.
            if (response.WantsSnapshot
                && await db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == claimed.SubscriptionId, ct) is { } subscription)
                RequestResync(subscription, now);
        }
        else if (now - claimed.CreatedAt >= RetryWindow)
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.LastError = Truncate(response.Error);
            // O consumidor continua com problema: as demais do lote também esperam, em vez de cada uma levar o timeout.
            retryAt = now + Backoff(delivery.Attempts, Random.Shared.NextDouble());
            logger.LogWarning("Entrega {Delivery} para {Url} desistida após 24 h ({Attempts} tentativas): {Error}",
                delivery.Id, WebhookUrl.Mask(claimed.Url), delivery.Attempts, response.Error);
        }
        else
        {
            retryAt = now + Backoff(delivery.Attempts, Random.Shared.NextDouble());
            delivery.NextAttemptAt = retryAt.Value;
            delivery.LastError = Truncate(response.Error);
            logger.LogInformation("Entrega para {Url} falhou ({Error}); nova tentativa em {RetryAt:O}.",
                WebhookUrl.Mask(claimed.Url), response.Error, retryAt);
        }
        await db.SaveChangesAsync(ct);
        if (response.WantsSnapshot)
            signal.Notify();
        return (true, retryAt);
    }

    private async Task ReleaseAsync(List<Guid> ids, DateTimeOffset retryAt, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.WebhookDeliveries
            .Where(d => ids.Contains(d.Id) && d.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, retryAt), ct);
    }

    private static string? Truncate(string? text) => text is { Length: > 1000 } ? text[..1000] : text;
}

/// <summary>Laço do worker: roda uma rodada, dorme até o sinal, o próximo horário devido ou 5 s.</summary>
public sealed class WebhookWorker(WebhookDispatcher dispatcher, WebhookSignal signal, TimeProvider clock,
    ILogger<WebhookWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = PollInterval;
            try
            {
                if (await dispatcher.RunOnceAsync(stoppingToken, waitForSends: false) >= WebhookDispatcher.BatchSize)
                    continue; // ainda há fila: segue sem dormir
                if (await dispatcher.NextDueAsync(stoppingToken) is { } next)
                {
                    var untilNext = next - clock.GetUtcNow();
                    if (untilNext < wait)
                        wait = untilNext > TimeSpan.Zero ? untilNext : TimeSpan.Zero;
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Falha na rodada de entregas de webhooks; nova rodada em {Wait}.", PollInterval);
            }

            try
            {
                await signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
