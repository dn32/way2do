using System.Globalization;
using System.Text.Json;
using Way2Do.Api.Data;
using Way2Do.Api.Domain;

namespace Way2Do.Api.Tests.Domain;

/// <summary>
/// Testes dirigidos pelos casos compartilhados em <c>tests/fixtures</c>. A suíte do frontend lê os mesmos arquivos:
/// as duas implementações precisam concordar em todos os casos.
/// </summary>
public sealed class SharedFixtureTests
{
    private static JsonElement Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file))).RootElement;

    private static DateTimeOffset Ts(JsonElement e) =>
        DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string? NullableString(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;

    // ---------- regra "urgente" ----------

    public static TheoryData<string, string?, string?, bool> UrgencyCases()
    {
        var data = new TheoryData<string, string?, string?, bool>();
        foreach (var c in Load("urgencia.json").GetProperty("casos").EnumerateArray())
            data.Add(c.GetProperty("motivo").GetString()!, NullableString(c, "titulo"), NullableString(c, "descricao"), c.GetProperty("urgente").GetBoolean());
        return data;
    }

    [Theory]
    [MemberData(nameof(UrgencyCases))]
    public void Regra_urgente(string motivo, string? titulo, string? descricao, bool urgente)
    {
        Assert.True(UrgencyRule.IsUrgent(titulo, descricao) == urgente,
            $"\"{titulo}\" / \"{descricao}\": esperado {(urgente ? "urgente" : "não urgente")} ({motivo})");
    }

    // ---------- ordenação ----------

    public static TheoryData<string> OrderingScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var s in Load("ordenacao.json").GetProperty("cenarios").EnumerateArray())
            data.Add(s.GetProperty("nome").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(OrderingScenarios))]
    public void Ordenacao(string nome)
    {
        var scenario = Load("ordenacao.json").GetProperty("cenarios").EnumerateArray()
            .Single(s => s.GetProperty("nome").GetString() == nome);
        var tasks = scenario.GetProperty("tarefas").EnumerateArray().Select(t => new TodoItem
        {
            Id = Guid.Parse(t.GetProperty("id").GetString()!),
            Title = t.GetProperty("titulo").GetString()!,
            Description = NullableString(t, "descricao"),
            Status = Enum.Parse<TodoStatus>(t.GetProperty("status").GetString()!, ignoreCase: true),
            Priority = Enum.Parse<TodoPriority>(t.GetProperty("prioridade").GetString()!, ignoreCase: true),
            DueDate = NullableString(t, "vencimento") is { } due ? DateOnly.Parse(due, CultureInfo.InvariantCulture) : null,
            CreatedAt = Ts(t.GetProperty("criadaEm")),
        }).ToList();

        var ordered = TodoOrdering.Sort(tasks).Select(t => t.Id.ToString("D")).ToList();

        var expected = scenario.GetProperty("esperado").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(expected, ordered);
    }

    // ---------- merge ----------

    public static TheoryData<string> MergeScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var s in Load("merge.json").GetProperty("cenarios").EnumerateArray())
            data.Add(s.GetProperty("nome").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(MergeScenarios))]
    public void Merge(string nome)
    {
        var scenario = Load("merge.json").GetProperty("cenarios").EnumerateArray()
            .Single(s => s.GetProperty("nome").GetString() == nome);
        var now = Ts(scenario.GetProperty("agora"));
        var target = scenario.GetProperty("atual").ValueKind == JsonValueKind.Null
            ? new TodoItem { Id = Guid.NewGuid(), Title = "" }
            : ToItem(scenario.GetProperty("atual"));
        var change = ToChange(scenario.GetProperty("alteracao"));

        MergeResult result = default;
        if (scenario.TryGetProperty("erro", out var expectedError))
        {
            var error = Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(target, change, now));
            Assert.Equal(expectedError.GetString(), error.Field);
        }
        else
        {
            result = TodoMerge.Apply(target, change, now);
        }

        var expected = scenario.GetProperty("esperado");
        if (expected.ValueKind == JsonValueKind.Null)
            AssertUntouched(target); // nada foi criado
        else
            AssertState(expected, target);
        Assert.Equal(scenario.GetProperty("mudou").GetBoolean(), result.Changed);
        Assert.Equal(scenario.GetProperty("conteudoMudou").GetBoolean(), result.ContentChanged);
    }

    // Monta o estado direto nas propriedades, sem passar pelo código sob teste.
    private static TodoItem ToItem(JsonElement state)
    {
        var item = new TodoItem { Id = Guid.NewGuid(), Title = "", CreatedAt = Ts(state.GetProperty("criadaEm")) };
        foreach (var field in state.GetProperty("campos").EnumerateObject())
        {
            var value = field.Value.GetProperty("valor");
            var text = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            switch (field.Name)
            {
                case TodoMerge.Title: item.Title = text!; break;
                case TodoMerge.Description: item.Description = text; break;
                case TodoMerge.DueDate: item.DueDate = text is null ? null : DateOnly.Parse(text, CultureInfo.InvariantCulture); break;
                case TodoMerge.Priority: item.Priority = Enum.Parse<TodoPriority>(text!, ignoreCase: true); break;
                case TodoMerge.Status: item.Status = Enum.Parse<TodoStatus>(text!, ignoreCase: true); break;
            }
            item.FieldTimestamps[field.Name] = Ts(field.Value.GetProperty("ts"));
        }
        foreach (var tag in state.GetProperty("tags").EnumerateObject())
            item.Tags[tag.Name] = new TagState(tag.Value.GetProperty("presente").GetBoolean(), Ts(tag.Value.GetProperty("ts")));
        var deleted = state.GetProperty("excluidaEm");
        item.DeletedAt = deleted.ValueKind == JsonValueKind.Null ? null : Ts(deleted);
        return item;
    }

    private static void AssertUntouched(TodoItem t)
    {
        Assert.Equal(default, t.CreatedAt);
        Assert.Null(t.DeletedAt);
        Assert.Empty(t.FieldTimestamps);
        Assert.Empty(t.Tags);
    }

    private static TodoChange ToChange(JsonElement e) => new()
    {
        Fields = e.TryGetProperty("campos", out var fields)
            ? fields.EnumerateObject().ToDictionary(f => f.Name, f => new FieldChange(f.Value.GetProperty("valor").Clone(), Ts(f.Value.GetProperty("ts"))))
            : new Dictionary<string, FieldChange>(),
        Tags = e.TryGetProperty("tags", out var tags)
            ? tags.EnumerateObject().ToDictionary(t => t.Name, t => new TagState(t.Value.GetProperty("presente").GetBoolean(), Ts(t.Value.GetProperty("ts"))))
            : new Dictionary<string, TagState>(),
        DeletedAt = e.TryGetProperty("excluidaEm", out var d) && d.ValueKind != JsonValueKind.Null ? Ts(d) : null,
        CreatedAt = e.TryGetProperty("criadaEm", out var c) && c.ValueKind != JsonValueKind.Null ? Ts(c) : null,
    };

    private static void AssertState(JsonElement expected, TodoItem actual)
    {
        Assert.Equal(Ts(expected.GetProperty("criadaEm")), actual.CreatedAt);

        var deleted = expected.GetProperty("excluidaEm");
        Assert.Equal(deleted.ValueKind == JsonValueKind.Null ? null : Ts(deleted), actual.DeletedAt);

        var fields = expected.GetProperty("campos").EnumerateObject().ToList();
        Assert.Equal(fields.Select(f => f.Name).Order(), actual.FieldTimestamps.Keys.Order());
        foreach (var field in fields)
        {
            Assert.Equal(Ts(field.Value.GetProperty("ts")), actual.FieldTimestamps[field.Name]);
            var value = field.Value.GetProperty("valor");
            Assert.Equal(value.ValueKind == JsonValueKind.Null ? null : value.GetString(), FieldValue(actual, field.Name));
        }

        var tags = expected.GetProperty("tags").EnumerateObject().ToList();
        Assert.Equal(tags.Select(t => t.Name).Order(), actual.Tags.Keys.Order());
        foreach (var tag in tags)
            Assert.Equal(new TagState(tag.Value.GetProperty("presente").GetBoolean(), Ts(tag.Value.GetProperty("ts"))), actual.Tags[tag.Name]);
    }

    private static string? FieldValue(TodoItem t, string name) => name switch
    {
        TodoMerge.Title => t.Title,
        TodoMerge.Description => t.Description,
        TodoMerge.DueDate => t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TodoMerge.Priority => t.Priority.ToString().ToLowerInvariant(),
        TodoMerge.Status => t.Status.ToString().ToLowerInvariant(),
        _ => throw new ArgumentOutOfRangeException(name),
    };
}
