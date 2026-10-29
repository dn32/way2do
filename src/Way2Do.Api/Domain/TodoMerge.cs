using System.Globalization;
using System.Text.Json;
using Way2Do.Api.Data;

namespace Way2Do.Api.Domain;

/// <summary>Novo valor de um campo e o horário (do aparelho) em que foi alterado.</summary>
public sealed record FieldChange(JsonElement Value, DateTimeOffset Ts);

/// <summary>
/// Alteração de uma tarefa: o que veio do aparelho (uma operação da fila) ou o estado completo de outra cópia.
/// Campos e tags ausentes não são tocados. <see cref="CreatedAt"/> só vale na criação.
/// </summary>
public sealed record TodoChange
{
    public IReadOnlyDictionary<string, FieldChange> Fields { get; init; } = new Dictionary<string, FieldChange>();
    public IReadOnlyDictionary<string, TagState> Tags { get; init; } = new Dictionary<string, TagState>();
    public DateTimeOffset? DeletedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// <see cref="ContentChanged"/>: algo visível mudou (gera nova versão e evento da integração).
/// <see cref="Changed"/>: algo precisa ser gravado (inclui só metadados, como um timestamp que avançou).
/// </summary>
public readonly record struct MergeResult(bool Changed, bool ContentChanged);

/// <summary>Alteração recusada (tipo errado, enum desconhecido, campo inexistente, limite excedido).</summary>
public sealed class InvalidChangeException(string field, string reason) : Exception($"{field}: {reason}")
{
    public string Field { get; } = field;
    public string Reason { get; } = reason;
}

/// <summary>Limites de conteúdo da tarefa (escopo, seção 5), os mesmos no formulário do frontend.</summary>
public static class TodoLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 4000;
    public const int MaxTagLength = 40;
    public const int MaxTags = 20;
}

/// <summary>
/// Merge last-write-wins por campo (escopo, seção 5), o mesmo no servidor e no cliente:
/// <list type="bullet">
/// <item>cada campo fica com o valor de timestamp mais recente; empate exato → vence o maior valor na ordem de código
/// UTF-16, com nulo abaixo de qualquer valor (todos os aparelhos convergem para o mesmo resultado);</item>
/// <item>cada tag é decidida individualmente; empate exato → a presença vence;</item>
/// <item>exclusão vence edição: tarefa excluída não recebe mais alterações; entre duas exclusões, fica a mais antiga;
/// excluir uma tarefa que esta cópia nunca viu não cria nada;</item>
/// <item>timestamps são truncados em milissegundos (a precisão do JavaScript) e os do futuro são limitados a
/// <c>now</c>, para que um aparelho adiantado não vença todas as disputas para sempre;</item>
/// <item>a data de criação só é definida na criação e não muda depois.</item>
/// </list>
/// A alteração é validada inteira antes de qualquer escrita: se for recusada, a tarefa fica intocada.
/// Casos em <c>tests/fixtures/merge.json</c>.
/// </summary>
public static class TodoMerge
{
    public const string Title = "title";
    public const string Description = "description";
    public const string DueDate = "dueDate";
    public const string Priority = "priority";
    public const string Status = "status";

    public static readonly IReadOnlyList<string> FieldNames = [Title, Description, DueDate, Priority, Status];

    public static MergeResult Apply(TodoItem target, TodoChange change, DateTimeOffset now)
    {
        now = Truncate(now);

        // 1) Valida e normaliza toda a alteração, sem tocar na tarefa.
        var fields = change.Fields
            .Select(f => (Name: f.Key, Value: Parse(f.Key, f.Value.Value), Ts: Clamp(f.Value.Ts, now)))
            .ToList();
        var tags = NormalizeTags(change.Tags, now);
        var deletedAt = change.DeletedAt is { } d ? Clamp(d, now) : (DateTimeOffset?)null;
        var isNew = target.CreatedAt == default && target.FieldTimestamps.Count == 0;

        // 2) Exclusão vence edição.
        if (deletedAt is not null && isNew)
            return default; // nada a excluir: não cria um registro só para marcá-lo como excluído
        if (target.DeletedAt is not null)
        {
            if (deletedAt is null || deletedAt >= target.DeletedAt)
                return default;
            target.DeletedAt = deletedAt; // entre duas exclusões, fica a mais antiga
            return new MergeResult(Changed: true, ContentChanged: false);
        }
        if (deletedAt is not null)
        {
            target.DeletedAt = deletedAt;
            return new MergeResult(Changed: true, ContentChanged: true);
        }

        // 3) Decide os vencedores, ainda sem escrever.
        var winningFields = fields.Where(f => FieldWins(target, f.Name, f.Value, f.Ts)).ToList();
        var winningTags = tags.Where(t => TagWins(target, t.Key, t.Value)).ToList();

        // 4) Confere o resultado: título obrigatório na criação, limite de tags visíveis.
        if (isNew && winningFields.All(f => f.Name != Title))
            throw new InvalidChangeException(Title, "obrigatório");
        var visibleTags = target.Tags.Where(t => t.Value.Present).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, tag) in winningTags)
        {
            if (tag.Present)
                visibleTags.Add(name);
            else
                visibleTags.Remove(name);
        }
        if (visibleTags.Count > TodoLimits.MaxTags)
            throw new InvalidChangeException("tags", $"no máximo {TodoLimits.MaxTags} tags");

        // 5) Aplica.
        var changed = false;
        var contentChanged = false;
        if (isNew)
        {
            target.CreatedAt = change.CreatedAt is { } created ? Clamp(created, now) : now;
            changed = contentChanged = true;
        }

        foreach (var (name, value, ts) in winningFields)
        {
            if (!target.FieldTimestamps.TryGetValue(name, out var currentTs) || currentTs != ts)
            {
                target.FieldTimestamps[name] = ts;
                changed = true;
            }
            if (!string.Equals(value, Canonical(target, name), StringComparison.Ordinal))
            {
                Assign(target, name, value);
                changed = contentChanged = true;
            }
        }

        foreach (var (name, tag) in winningTags)
        {
            var wasVisible = target.Tags.TryGetValue(name, out var current) && current.Present;
            if (wasVisible != tag.Present)
                contentChanged = true;
            target.Tags[name] = tag;
            changed = true;
        }

        return new MergeResult(changed, contentChanged);
    }

    /// <summary>
    /// Identidade da tag: forma NFC, sem espaços nas pontas e em minúsculas (<c>Obra</c>, <c> obra </c> e a forma
    /// decomposta de <c>Ação</c> são a mesma tag). Equivale, no frontend, a
    /// <c>tag.normalize('NFC').trim().toLowerCase().replace(/ς/g, 'σ')</c>.
    /// </summary>
    public static string NormalizeTag(string tag)
    {
        if (!TextNormalizer.IsStorable(tag))
            throw new InvalidChangeException("tags", "caractere inválido");
        var name = TextNormalizer.LowerLikeJavaScript(
            TextNormalizer.TrimLikeJavaScript(tag.Normalize(System.Text.NormalizationForm.FormC)));
        if (name.Length == 0)
            throw new InvalidChangeException("tags", "tag vazia");
        if (name.Length > TodoLimits.MaxTagLength)
            throw new InvalidChangeException("tags", $"tag com mais de {TodoLimits.MaxTagLength} caracteres");
        return name;
    }

    /// <summary>Tags visíveis: as presentes, em ordem.</summary>
    public static IReadOnlyList<string> VisibleTags(TodoItem item) =>
        item.Tags.Where(t => t.Value.Present).Select(t => t.Key).Order(StringComparer.Ordinal).ToList();

    private static bool FieldWins(TodoItem target, string name, string? value, DateTimeOffset ts)
    {
        if (!target.FieldTimestamps.TryGetValue(name, out var currentTs) || ts > currentTs)
            return true;
        return ts == currentTs && CompareValues(value, Canonical(target, name)) > 0;
    }

    private static bool TagWins(TodoItem target, string name, TagState incoming) =>
        !target.Tags.TryGetValue(name, out var current) ||
        incoming.Ts > current.Ts ||
        (incoming.Ts == current.Ts && incoming.Present && !current.Present);

    // Duas grafias da mesma tag na mesma alteração: fica a que venceria a disputa entre elas.
    private static Dictionary<string, TagState> NormalizeTags(IReadOnlyDictionary<string, TagState> tags, DateTimeOffset now)
    {
        var result = new Dictionary<string, TagState>(StringComparer.Ordinal);
        foreach (var (raw, tag) in tags)
        {
            var name = NormalizeTag(raw);
            var incoming = tag with { Ts = Clamp(tag.Ts, now) };
            if (!result.TryGetValue(name, out var other) || incoming.Ts > other.Ts ||
                (incoming.Ts == other.Ts && incoming.Present && !other.Present))
                result[name] = incoming;
        }
        return result;
    }

    private static DateTimeOffset Truncate(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private static DateTimeOffset Clamp(DateTimeOffset ts, DateTimeOffset now)
    {
        var truncated = Truncate(ts);
        return truncated > now ? now : truncated;
    }

    // Nulo fica abaixo de qualquer valor; entre valores, ordem de código UTF-16 (a mesma do JavaScript).
    private static int CompareValues(string? a, string? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        _ => Math.Sign(string.CompareOrdinal(a, b)),
    };

    // Forma canônica (texto) de cada campo: é o que se compara no desempate e o que se grava.
    private static string? Canonical(TodoItem t, string name) => name switch
    {
        Title => t.Title,
        Description => t.Description,
        DueDate => t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Priority => TodoFormat.Priority(t.Priority),
        Status => TodoFormat.Status(t.Status),
        _ => throw new InvalidChangeException(name, "campo desconhecido"),
    };

    private static string? Parse(string name, JsonElement value)
    {
        string? text;
        try
        {
            text = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => throw new InvalidChangeException(name, "valor precisa ser texto ou nulo"),
            };
        }
        catch (InvalidOperationException)
        {
            // O JSON trouxe um escape de UTF-16 inválido (ex.: "\uD800" sem par): o texto não pode ser lido.
            throw new InvalidChangeException(name, "caractere inválido");
        }
        if (text is not null && !TextNormalizer.IsStorable(text))
            throw new InvalidChangeException(name, "caractere inválido");

        switch (name)
        {
            case Title:
                if (text is null || TextNormalizer.TrimLikeJavaScript(text).Length == 0)
                    throw new InvalidChangeException(name, "obrigatório");
                return text.Length <= TodoLimits.MaxTitleLength
                    ? text
                    : throw new InvalidChangeException(name, $"no máximo {TodoLimits.MaxTitleLength} caracteres");
            case Description:
                return text is null || text.Length <= TodoLimits.MaxDescriptionLength
                    ? text
                    : throw new InvalidChangeException(name, $"no máximo {TodoLimits.MaxDescriptionLength} caracteres");
            case DueDate:
                if (text is null)
                    return null;
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? text
                    : throw new InvalidChangeException(name, "data no formato AAAA-MM-DD");
            case Priority:
                return text is "low" or "medium" or "high" ? text : throw new InvalidChangeException(name, "low, medium ou high");
            case Status:
                return text is "pending" or "done" ? text : throw new InvalidChangeException(name, "pending ou done");
            default:
                throw new InvalidChangeException(name, "campo desconhecido");
        }
    }

    private static void Assign(TodoItem t, string name, string? value)
    {
        switch (name)
        {
            case Title: t.Title = value!; break;
            case Description: t.Description = value; break;
            case DueDate: t.DueDate = value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case Priority: t.Priority = Enum.Parse<TodoPriority>(value!, ignoreCase: true); break;
            case Status: t.Status = Enum.Parse<TodoStatus>(value!, ignoreCase: true); break;
        }
    }
}
