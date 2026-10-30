using System.Diagnostics;
using Way2Do.Api.Data;

namespace Way2Do.Api.Domain;

/// <summary>
/// Forma pública (texto) dos enums da tarefa, nos dois sentidos: a mesma no merge, no banco, no pull e no payload do
/// webhook (contrato público). Um valor novo no enum que não esteja aqui falha na hora, em vez de sair com o texto de
/// outro valor.
/// </summary>
public static class TodoFormat
{
    public static string Priority(TodoPriority priority) => priority switch
    {
        TodoPriority.Low => "low",
        TodoPriority.Medium => "medium",
        TodoPriority.High => "high",
        _ => throw new UnreachableException($"TodoPriority sem texto: {priority}"),
    };

    public static string Status(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.Done => "done",
        _ => throw new UnreachableException($"TodoStatus sem texto: {status}"),
    };

    /// <summary>Prioridade a partir do texto do contrato; <c>null</c> se o texto não for uma das válidas.</summary>
    public static TodoPriority? ParsePriority(string? text) => text switch
    {
        "low" => TodoPriority.Low,
        "medium" => TodoPriority.Medium,
        "high" => TodoPriority.High,
        _ => null,
    };

    /// <summary>Status a partir do texto do contrato; <c>null</c> se o texto não for um dos válidos.</summary>
    public static TodoStatus? ParseStatus(string? text) => text switch
    {
        "pending" => TodoStatus.Pending,
        "done" => TodoStatus.Done,
        _ => null,
    };

    // Leitura do banco: o valor gravado veio desta mesma classe. Maiúsculas são toleradas (um ajuste manual no banco
    // não derruba a sincronização do usuário); um texto desconhecido é dado corrompido.
    public static TodoPriority PriorityFromDb(string text) =>
        ParsePriority(text.ToLowerInvariant()) ?? throw new InvalidOperationException($"Prioridade inválida no banco: '{text}'.");

    public static TodoStatus StatusFromDb(string text) =>
        ParseStatus(text.ToLowerInvariant()) ?? throw new InvalidOperationException($"Status inválido no banco: '{text}'.");
}
