using System.Text.Json;
using Way2Do.Api.Data;
using Way2Do.Api.Domain;

namespace Way2Do.Api.Tests.Domain;

public sealed class TodoMergeValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);

    private static TodoChange FieldChange(string field, string json) => new()
    {
        Fields = new Dictionary<string, FieldChange> { [field] = new(JsonDocument.Parse(json).RootElement, Now.AddHours(-1)) },
    };

    [Theory]
    [InlineData("color", "\"azul\"", "campo desconhecido")]
    [InlineData("priority", "\"urgentissima\"", "low, medium ou high")]
    [InlineData("status", "\"cancelada\"", "pending ou done")]
    [InlineData("title", "null", "obrigatório")]
    [InlineData("dueDate", "\"29/10/2026\"", "AAAA-MM-DD")]
    [InlineData("dueDate", "\"2026-02-30\"", "AAAA-MM-DD")]
    [InlineData("title", "42", "texto ou nulo")]
    public void Valor_invalido_e_recusado(string field, string json, string reason)
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "Original", CreatedAt = Now.AddDays(-1) };

        var error = Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, FieldChange(field, json), Now));

        Assert.Equal(field, error.Field);
        Assert.Contains(reason, error.Reason);
    }

    [Theory]
    [InlineData("title", 200, true)]
    [InlineData("title", 201, false)]
    [InlineData("description", 4000, true)]
    [InlineData("description", 4001, false)]
    public void Limites_de_tamanho(string field, int length, bool accepted)
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "Original", CreatedAt = Now.AddDays(-1) };
        var change = FieldChange(field, JsonSerializer.Serialize(new string('a', length)));

        if (accepted)
            TodoMerge.Apply(task, change, Now);
        else
            Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, change, Now));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Titulo_em_branco_e_recusado(string json)
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "Original", CreatedAt = Now.AddDays(-1) };

        var error = Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, FieldChange("title", json), Now));
        Assert.Equal("title", error.Field);
    }

    [Fact]
    public void Tag_acima_de_40_caracteres_e_recusada()
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
        var change = new TodoChange { Tags = new Dictionary<string, TagState> { [new string('t', 41)] = new(true, Now) } };

        Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, change, Now));
    }

    [Fact]
    public void Mais_de_20_tags_visiveis_e_recusado_sem_alterar_a_tarefa()
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
        for (var i = 0; i < 20; i++)
            task.Tags[$"tag{i}"] = new TagState(true, Now.AddHours(-2));
        var change = new TodoChange { Tags = new Dictionary<string, TagState> { ["tag20"] = new(true, Now) } };

        Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, change, Now));
        Assert.Equal(20, task.Tags.Count);
    }

    [Fact]
    public void Remover_uma_tag_libera_espaco_para_outra_na_mesma_alteracao()
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
        for (var i = 0; i < 20; i++)
            task.Tags[$"tag{i}"] = new TagState(true, Now.AddHours(-2));
        var change = new TodoChange
        {
            Tags = new Dictionary<string, TagState> { ["tag0"] = new(false, Now), ["nova"] = new(true, Now) },
        };

        TodoMerge.Apply(task, change, Now);

        Assert.Equal(20, TodoMerge.VisibleTags(task).Count);
        Assert.Contains("nova", TodoMerge.VisibleTags(task));
    }

    [Fact]
    public void Timestamps_sao_truncados_em_milissegundos()
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
        var withTicks = new DateTimeOffset(2026, 10, 29, 11, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        var change = new TodoChange
        {
            Fields = new Dictionary<string, FieldChange> { ["title"] = new(JsonDocument.Parse("\"y\"").RootElement, withTicks) },
        };

        TodoMerge.Apply(task, change, Now.AddTicks(9_999));

        Assert.Equal(new DateTimeOffset(2026, 10, 29, 11, 0, 0, 123, TimeSpan.Zero), task.FieldTimestamps["title"]);
    }

    [Fact]
    public void Grafias_diferentes_da_mesma_tag_viram_uma_so()
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
        var decomposed = "Ac\u0327a\u0303o"; // "Ação" em NFD
        var change = new TodoChange
        {
            Tags = new Dictionary<string, TagState>
            {
                [decomposed] = new(true, Now.AddHours(-1)),
                ["\uFEFF ação "] = new(false, Now.AddHours(-2)),
            },
        };

        TodoMerge.Apply(task, change, Now);

        Assert.Equal(["ação"], task.Tags.Keys);
        Assert.True(task.Tags["ação"].Present);
    }

    [Fact]
    public void Sigma_final_e_sigma_comum_sao_a_mesma_tag()
    {
        // O frontend troca ς por σ depois do toLowerCase(); o servidor faz o mesmo, senão viram duas tags.
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
        var change = new TodoChange
        {
            Tags = new Dictionary<string, TagState> { ["λογος"] = new(true, Now.AddHours(-1)), ["λογoς"] = new(true, Now.AddHours(-2)) },
        };

        TodoMerge.Apply(task, change, Now);

        Assert.Equal(["λογoσ", "λογοσ"], task.Tags.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(task.Tags.Keys, k => k.Contains('ς'));
    }

    [Theory]
    [InlineData("\"\\uFEFF\"")]
    [InlineData("\"\\uFEFF \\u3000\"")]
    public void Titulo_so_com_espacos_invisiveis_e_recusado(string json)
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "Original", CreatedAt = Now.AddDays(-1) };

        var error = Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, FieldChange("title", json), Now));
        Assert.Equal("obrigatório", error.Reason);
    }

    [Theory]
    [InlineData("title", "\"abc\\u0000def\"")]
    [InlineData("description", "\"abc\\u0000\"")]
    [InlineData("title", "\"abc\\uD800def\"")]
    [InlineData("description", "\"\\uDC00\"")]
    public void Texto_que_o_banco_nao_grava_e_recusado(string field, string json)
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "Original", CreatedAt = Now.AddDays(-1) };

        var error = Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, FieldChange(field, json), Now));
        Assert.Equal("caractere inválido", error.Reason);
        Assert.Equal("Original", task.Title);
    }

    // [Fact] e não [InlineData]: o xUnit serializa os dados da teoria e troca o surrogate isolado por U+FFFD.
    [Fact]
    public void Tag_com_caractere_que_o_banco_nao_grava_e_recusada()
    {
        foreach (var tag in new[] { "obra" + '\u0000', "obra" + '\uD800', '\uDC00' + "obra" })
        {
            var task = new TodoItem { Id = Guid.NewGuid(), Title = "x", CreatedAt = Now.AddDays(-1) };
            var change = new TodoChange { Tags = new Dictionary<string, TagState> { [tag] = new(true, Now) } };

            var error = Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, change, Now));
            Assert.Equal("caractere inválido", error.Reason);
            Assert.Empty(task.Tags);
        }
    }

    [Fact]
    public void Tag_vazia_e_recusada()
    {
        var task = new TodoItem { Id = Guid.NewGuid(), Title = "Original", CreatedAt = Now.AddDays(-1) };
        var change = new TodoChange { Tags = new Dictionary<string, TagState> { ["  "] = new(true, Now) } };

        Assert.Throws<InvalidChangeException>(() => TodoMerge.Apply(task, change, Now));
    }

    [Fact]
    public void Tags_visiveis_sao_as_presentes_em_ordem()
    {
        var task = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "x",
            Tags = new()
            {
                ["obra"] = new(true, Now),
                ["cliente"] = new(true, Now),
                ["antiga"] = new(false, Now),
            },
        };

        Assert.Equal(["cliente", "obra"], TodoMerge.VisibleTags(task));
    }
}
