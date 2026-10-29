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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
        {
            var evt = new OutboxEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                UserId = userId,
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
