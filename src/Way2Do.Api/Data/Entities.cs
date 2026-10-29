namespace Way2Do.Api.Data;

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Sempre em minúsculas: o login não diferencia maiúsculas.</summary>
    public required string UserName { get; set; }

    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum TodoPriority { Low, Medium, High }

public enum TodoStatus { Pending, Done }

/// <summary>Estado de uma tag no merge por item: presente ou removida, e quando.</summary>
public sealed record TagState(bool Present, DateTimeOffset Ts);

public sealed class TodoItem
{
    /// <summary>Gerado no cliente: permite criar offline e torna o push idempotente.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    public TodoPriority Priority { get; set; } = TodoPriority.Medium;
    public TodoStatus Status { get; set; } = TodoStatus.Pending;

    /// <summary>Tag → estado; as removidas ficam como tombstone.</summary>
    public Dictionary<string, TagState> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Campo → timestamp da última alteração; base do LWW por campo.</summary>
    public Dictionary<string, DateTimeOffset> FieldTimestamps { get; set; } = [];

    /// <summary>Incrementa a cada alteração efetiva no servidor; vai nos eventos da integração.</summary>
    public int Version { get; set; }

    /// <summary>Posição na sequência global; cursor do pull.</summary>
    public long ChangeSeq { get; set; }
}

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public Guid UserId { get; set; }
    public Guid? TaskId { get; set; }
    public int? TaskVersion { get; set; }
    public long? ChangeSeq { get; set; }

    /// <summary>Corpo JSON pronto para envio, montado na mesma transação da alteração.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>URL de entrada do consumidor; contém o token, então é tratada como segredo.</summary>
    public required string Url { get; set; }

    public string? DashboardUrl { get; set; }
    public DateTimeOffset? LastResyncAt { get; set; }

    /// <summary>
    /// Retrato completo pedido (205) e ainda não enfileirado: no máximo um por minuto, então um pedido dentro do
    /// intervalo fica agendado para o fim dele, e pedidos repetidos se juntam neste mesmo horário.
    /// </summary>
    public DateTimeOffset? ResyncDueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum DeliveryStatus { Pending, Delivered, Failed }

public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid SubscriptionId { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
