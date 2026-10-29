namespace Way2Do.Consumer.Spaces;

/// <summary>Tipos de evento do contrato público do webhook (docs/escopo.md, seção 6).</summary>
public static class EventTypes
{
    public const string Created = "todo.created";
    public const string Updated = "todo.updated";
    public const string Deleted = "todo.deleted";
    public const string Snapshot = "todo.snapshot";
    public const string Ping = "webhook.ping";

    /// <summary>Chave em que se contam os tipos desconhecidos (o painel não cria uma chave por texto recebido).</summary>
    public const string Other = "outros";

    public static bool IsTodoEvent(string type) => type is Created or Updated or Deleted or Snapshot;

    public static bool IsKnown(string type) => type == Ping || IsTodoEvent(type);
}

/// <summary>Envelope do evento. Campos anuláveis para que a ausência vire 400, e não um valor padrão silencioso.</summary>
public sealed record WebhookEvent(Guid? EventId, string? Type, DateTimeOffset? OccurredAt, TodoPayload? Todo)
{
    /// <summary>Motivo da recusa, ou null se o evento é válido.</summary>
    public string? Validate()
    {
        if (EventId is null || EventId == Guid.Empty)
            return "eventId ausente";
        if (string.IsNullOrWhiteSpace(Type))
            return "type ausente";
        if (OccurredAt is null)
            return "occurredAt ausente";
        if (EventTypes.IsTodoEvent(Type))
        {
            if (Todo is null)
                return "todo ausente";
            if (Todo.Id is null || Todo.Id == Guid.Empty)
                return "todo.id ausente";
            if (Todo.Version is null)
                return "todo.version ausente";
        }
        return null;
    }
}

/// <summary>Estado completo da tarefa (event-carried state transfer), como o Way2Do envia.</summary>
public sealed record TodoPayload(
    Guid? Id,
    Guid? UserId,
    string? UserName,
    long? Version,
    string? Title,
    string? Description,
    DateOnly? DueDate,
    string? Priority,
    string? Status,
    IReadOnlyList<string>? Tags,
    bool Urgent,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DeletedAt);
