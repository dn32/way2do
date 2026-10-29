using System.Text.Json;
using Way2Do.Api.Data;
using Way2Do.Api.Domain;

namespace Way2Do.Api.Sync;

/// <summary>
/// Lote de operações da fila do aparelho. Cada operação chega como JSON bruto e é lida isoladamente: uma operação
/// malformada é recusada sozinha, sem derrubar o lote (senão a fila do aparelho travaria reenviando o mesmo lote).
/// </summary>
public sealed record PushRequest(IReadOnlyList<JsonElement>? Operations);

/// <summary>
/// Uma operação da fila: campos e tags alterados, cada um com o horário (do aparelho) da alteração.
/// <see cref="CreatedAt"/> é a data de criação da tarefa e só vale quando o servidor ainda não a conhece.
/// </summary>
public sealed record PushOperation(
    Guid OpId,
    Guid TaskId,
    DateTimeOffset? CreatedAt,
    Dictionary<string, PushFieldChange?>? Fields,
    Dictionary<string, TagState?>? Tags,
    DateTimeOffset? DeletedAt);

public sealed record PushFieldChange(JsonElement Value, DateTimeOffset Ts);

public sealed record PushResponse(IReadOnlyList<PushResult> Results);

/// <summary>
/// Resultado de uma operação. Recusada: traz o motivo e o estado da tarefa no servidor ao fim do lote (<c>null</c>
/// se o servidor não tem a tarefa), que o aparelho usa para substituir a cópia local e tirar a operação da fila.
/// </summary>
public sealed record PushResult(Guid OpId, Guid TaskId, string Status, string? Field, string? Reason, SyncTask? Task)
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public sealed record PullResponse(IReadOnlyList<SyncTask> Tasks, long Cursor, bool HasMore);

/// <summary>Estado completo de uma tarefa, com os metadados que o aparelho precisa para o merge.</summary>
public sealed record SyncTask(
    Guid Id,
    string Title,
    string? Description,
    DateOnly? DueDate,
    string Priority,
    string Status,
    IReadOnlyDictionary<string, TagState> Tags,
    IReadOnlyDictionary<string, DateTimeOffset> FieldTimestamps,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    int Version,
    long ChangeSeq)
{
    public static SyncTask From(TodoItem t) => new(
        t.Id,
        t.Title,
        t.Description,
        t.DueDate,
        TodoFormat.Priority(t.Priority),
        TodoFormat.Status(t.Status),
        new Dictionary<string, TagState>(t.Tags),
        new Dictionary<string, DateTimeOffset>(t.FieldTimestamps),
        t.CreatedAt,
        t.DeletedAt,
        t.Version,
        t.ChangeSeq);
}

internal static class PushOperationReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Lê uma operação; devolve o motivo da recusa quando ela está malformada.</summary>
    public static (PushOperation? Operation, string? Error) Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return (null, "operação precisa ser um objeto");

        PushOperation? op;
        try
        {
            op = element.Deserialize<PushOperation>(Json);
        }
        catch (JsonException)
        {
            return (null, "operação malformada (tipo ou data inválidos)");
        }

        if (op is null || op.TaskId == Guid.Empty)
            return (op, "taskId obrigatório");
        if (op.Fields?.Values.Any(f => f is null) == true)
            return (op, "campo sem valor e horário");
        if (op.Tags?.Values.Any(t => t is null) == true)
            return (op, "tag sem estado e horário");
        return (op, null);
    }

    /// <summary>Melhor esforço para identificar uma operação malformada na resposta.</summary>
    public static Guid IdOrEmpty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var p) &&
        p.ValueKind == JsonValueKind.String &&
        Guid.TryParse(p.GetString(), out var id)
            ? id
            : Guid.Empty;

    public static TodoChange ToChange(this PushOperation op) => new()
    {
        Fields = (op.Fields ?? []).ToDictionary(f => f.Key, f => new FieldChange(f.Value!.Value, f.Value.Ts)),
        Tags = (op.Tags ?? []).ToDictionary(t => t.Key, t => t.Value!),
        DeletedAt = op.DeletedAt,
        CreatedAt = op.CreatedAt,
    };
}
