using Way2Do.Api.Data;

namespace Way2Do.Api.Domain;

/// <summary>
/// Forma pública (texto) dos enums da tarefa: a mesma no merge, no pull e no payload do webhook (contrato público).
/// </summary>
public static class TodoFormat
{
    public static string Priority(TodoPriority priority) => priority switch
    {
        TodoPriority.Low => "low",
        TodoPriority.Medium => "medium",
        _ => "high",
    };

    public static string Status(TodoStatus status) => status == TodoStatus.Pending ? "pending" : "done";
}
