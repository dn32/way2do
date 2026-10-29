using System.Net;
using Way2Do.Consumer.Spaces;

namespace Way2Do.Consumer.Tests;

public sealed class SpaceKeysTests
{
    [Fact]
    public void Espaco_e_o_SHA256_do_token_em_hex_minusculo()
    {
        // Valor de referência calculado fora do .NET (sha256sum).
        Assert.Equal("806c4b3f3fad6c58384d9329ecd44f6a221d95c0e51b2c73d4a00cf156f7319b",
            SpaceKeys.SpaceIdOf("Way2Do-token-de-teste_0123456789abcdefghijk"));
    }

    [Fact]
    public void Token_novo_tem_256_bits_em_base64url_sem_padding()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => SpaceKeys.NewToken()).ToList();

        Assert.All(tokens, t => Assert.True(SpaceKeys.IsValidToken(t), t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(SpaceKeys.IsValidSpaceId(SpaceKeys.SpaceIdOf(t))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghijk\n")]
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghij=")]
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghij/")]
    public void Token_fora_do_formato_e_invalido(string? token) => Assert.False(SpaceKeys.IsValidToken(token));
}

public sealed class LimitsTests
{
    [Fact]
    public async Task Limite_de_espacos_recusa_novos_com_503_e_mantem_os_existentes()
    {
        using var factory = new ConsumerFactory(limits: new SpaceLimits(MaxSpaces: 3));
        var client = factory.CreateClient();
        var tokens = Enumerable.Range(0, 3).Select(_ => Events.NewToken()).ToList();
        foreach (var token in tokens)
            Assert.Equal(HttpStatusCode.ResetContent, (await client.Send(token, Events.Ping())).StatusCode);

        var extra = Events.NewToken();
        var refused = await client.Send(extra, Events.Ping());
        var existing = await client.Send(tokens[0], Events.Ping());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v/{SpaceKeys.SpaceIdOf(extra)}/state")).StatusCode);
    }

    [Fact]
    public async Task Criacoes_simultaneas_nao_passam_do_limite_de_espacos()
    {
        using var factory = new ConsumerFactory(limits: new SpaceLimits(MaxSpaces: 5));
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => client.Send(Events.NewToken(), Events.Ping())));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.ResetContent));
        Assert.Equal(35, responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task Primeiro_evento_concorrente_pede_o_retrato_uma_vez_so()
    {
        using var factory = new ConsumerFactory();
        var client = factory.CreateClient();
        var token = Events.NewToken();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.ResetContent);
        var state = await client.State(token);
        Assert.Equal(20, state.Todos.Count);
        Assert.Equal(1, state.Health.SnapshotsRequested);
    }

    [Fact]
    public async Task Tarefa_nova_alem_do_limite_e_descartada_mas_as_existentes_seguem_atualizando()
    {
        using var factory = new ConsumerFactory(limits: new SpaceLimits(MaxTodosPerSpace: 2));
        var client = factory.CreateClient();
        var token = Events.NewToken();
        var first = Guid.NewGuid();
        await client.Send(token, Events.Todo(EventTypes.Created, first, 1));
        await client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1));

        var over = await client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1));
        var update = await client.Send(token, Events.Todo(EventTypes.Updated, first, 2, "atualizada"));

        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var state = await client.State(token);
        Assert.Equal(2, state.Todos.Count);
        Assert.Equal(1, state.Health.DroppedByLimit);
        Assert.Equal("atualizada", state.Todos.Single(t => t.Id == first).Title);
    }

    [Fact]
    public async Task EventIds_mais_antigos_sao_esquecidos_e_a_regra_de_versao_ainda_barra_a_reentrega()
    {
        using var factory = new ConsumerFactory(limits: new SpaceLimits(MaxSeenEventIds: 2));
        var client = factory.CreateClient();
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();
        var oldEvent = Guid.NewGuid();
        await client.Send(token, Events.Todo(EventTypes.Created, todoId, 1, "v1", oldEvent));
        await client.Send(token, Events.Ping());
        await client.Send(token, Events.Ping());

        // O id do primeiro evento já saiu da memória: não conta como duplicado, mas a versão 1 não volta.
        await client.Send(token, Events.Todo(EventTypes.Created, todoId, 1, "v1 reentregue", oldEvent));

        var state = await client.State(token);
        Assert.Equal(0, state.Health.Duplicates);
        Assert.Equal(1, state.Health.Stale);
        Assert.Equal("v1", Assert.Single(state.Todos).Title);
    }
}

public sealed class StartupTests
{
    [Theory]
    [InlineData("Consumer:IngestBaseUrl")]
    [InlineData("Consumer:ViewBaseUrl")]
    public void Sem_URL_base_o_consumidor_nao_sobe(string key)
    {
        using var factory = new ConsumerFactory(new Dictionary<string, string?> { [key] = "" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains($"{key} não configurada", Flatten(error));
    }

    [Theory]
    [InlineData("localhost:5200")]
    [InlineData("ftp://consumer")]
    [InlineData("http://consumer/?x=1")]
    public void URL_base_invalida_impede_a_subida(string value)
    {
        using var factory = new ConsumerFactory(new Dictionary<string, string?> { ["Consumer:IngestBaseUrl"] = value });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Consumer:IngestBaseUrl precisa ser uma URL http ou https", Flatten(error));
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        return string.Join(" | ", messages);
    }
}
