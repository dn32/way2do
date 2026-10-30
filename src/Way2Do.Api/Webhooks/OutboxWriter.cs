using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Way2Do.Api.Data;
using Way2Do.Api.Domain;

namespace Way2Do.Api.Webhooks;

/// <summary>
/// Grava, na transação da alteração (outbox transacional), o evento da integração e uma entrega por assinatura do
/// dono da tarefa. O envio em si é feito depois pelo worker de webhooks.
/// </summary>
public static class OutboxWriter
{
    public const string Created = "todo.created";
    public const string Updated = "todo.updated";
    public const string Deleted = "todo.deleted";
    public const string Snapshot = "todo.snapshot";
    public const string Ping = "webhook.ping";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Tipo do evento de uma tarefa alterada num lote do push (contrato público), ou <c>null</c> quando não há evento:
    /// nada visível mudou, ou a tarefa foi criada e excluída no mesmo lote (o consumidor nunca a viu).
    /// </summary>
    public static string? EventTypeFor(bool contentChanged, bool isNew, bool deletedBefore, bool deletedNow) =>
        !contentChanged || (isNew && deletedNow) ? null
        : isNew ? Created
        : deletedNow && !deletedBefore ? Deleted
        : Updated;

    public static async Task AddEventsAsync(AppDbContext db, Guid userId, string userName,
        IReadOnlyList<(TodoItem Task, string Type)> changes, DateTimeOffset now, CancellationToken ct)
    {
        if (changes.Count == 0)
            return;

        var subscriptions = await db.WebhookSubscriptions
            .Where(s => s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var (task, type) in changes)
            AddEvent(db, task, type, userName, subscriptions, now);
    }

    /// <summary>
    /// Retrato completo para UMA assinatura (o consumidor respondeu 205): um <c>todo.snapshot</c> por tarefa não
    /// excluída do dono dela, só o que a assinatura já tem direito de receber. Devolve quantos eventos entraram.
    /// </summary>
    public static async Task<int> AddSnapshotAsync(AppDbContext db, WebhookSubscription subscription, DateTimeOffset now,
        CancellationToken ct)
    {
        var userName = await db.Users.Where(u => u.Id == subscription.UserId).Select(u => u.UserName).SingleAsync(ct);
        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.UserId == subscription.UserId && t.DeletedAt == null)
            .OrderBy(t => t.ChangeSeq)
            .ToListAsync(ct);
        foreach (var task in tasks)
            AddEvent(db, task, Snapshot, userName, [subscription.Id], now);
        return tasks.Count;
    }

    /// <summary>Corpo do <c>webhook.ping</c> do cadastro (não passa pelo outbox: o cadastro espera a resposta).</summary>
    public static string PingPayload(Guid eventId, DateTimeOffset now) =>
        JsonSerializer.Serialize(new { eventId, type = Ping, occurredAt = now }, Json);

    private static void AddEvent(AppDbContext db, TodoItem task, string type, string userName,
        IReadOnlyList<Guid> subscriptions, DateTimeOffset now)
    {
        var evt = new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            UserId = task.UserId,
            TaskId = task.Id,
            TaskVersion = task.Version,
            ChangeSeq = task.ChangeSeq,
            OccurredAt = now,
            Payload = "",
        };
        evt.Payload = JsonSerializer.Serialize(new
        {
            eventId = evt.Id,
            type,
            occurredAt = now,
            todo = Describe(task, userName),
        }, Json);
        db.OutboxEvents.Add(evt);

        foreach (var subscriptionId in subscriptions)
        {
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                EventId = evt.Id,
                SubscriptionId = subscriptionId,
                Status = DeliveryStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }
    }

    // Estado completo da tarefa (event-carried state transfer): o consumidor não precisa consultar nada.
    private static object Describe(TodoItem t, string userName) => new
    {
        id = t.Id,
        userId = t.UserId,
        userName,
        version = t.Version,
        title = t.Title,
        description = t.Description,
        dueDate = t.DueDate,
        priority = TodoFormat.Priority(t.Priority),
        status = TodoFormat.Status(t.Status),
        tags = TodoMerge.VisibleTags(t),
        urgent = UrgencyRule.IsUrgent(t.Title, t.Description),
        createdAt = t.CreatedAt,
        deletedAt = t.DeletedAt,
    };
}
