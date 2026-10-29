using System.Net;
using System.Net.Http.Json;
using System.Text;
using Way2Do.Consumer.Spaces;

namespace Way2Do.Consumer.Tests;

/// <summary>Um consumidor por classe de testes; cada teste usa tokens novos, então os espaços não se misturam.</summary>
public sealed class SharedConsumer : IDisposable
{
    public ConsumerFactory Factory { get; } = new();

    public HttpClient Client() => Factory.CreateClient();

    public void Dispose() => Factory.Dispose();
}

public sealed class IngestTests(SharedConsumer consumer) : IClassFixture<SharedConsumer>
{
    private readonly HttpClient _client = consumer.Client();

    [Fact]
    public async Task Primeiro_evento_num_espaco_vazio_pede_o_retrato_com_205_e_os_seguintes_recebem_200()
    {
        var token = Events.NewToken();

        var first = await _client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1));
        var second = await _client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1));

        Assert.Equal(HttpStatusCode.ResetContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var state = await _client.State(token);
        Assert.Equal(2, state.Todos.Count);
        Assert.Equal(1, state.Health.SnapshotsRequested);
    }

    [Fact]
    public async Task Retrato_num_espaco_vazio_recebe_200_e_nunca_pede_outro_retrato()
    {
        var token = Events.NewToken();

        var response = await _client.Send(token, Events.Todo(EventTypes.Snapshot, Guid.NewGuid(), 3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await _client.State(token);
        Assert.Single(state.Todos);
        Assert.Equal(0, state.Health.SnapshotsRequested);
    }

    [Fact]
    public async Task Ping_do_cadastro_num_espaco_vazio_recebe_205_com_o_painel_no_cabecalho()
    {
        var token = Events.NewToken();
        var expected = $"{ConsumerFactory.ViewBase}/v/{SpaceKeys.SpaceIdOf(token)}";

        var response = await _client.Send(token, Events.Ping());

        // 205 não pode ter corpo (o Kestrel recusa a escrita): o dashboardUrl vai no cabeçalho.
        Assert.Equal(HttpStatusCode.ResetContent, response.StatusCode);
        Assert.Equal(expected, response.Headers.GetValues(IngestEndpoints.DashboardUrlHeader).Single());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Ping_num_espaco_existente_recebe_200_com_dashboardUrl_no_corpo()
    {
        var token = Events.NewToken();
        await _client.Send(token, Events.Ping());

        var response = await _client.Send(token, Events.Ping());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var expected = $"{ConsumerFactory.ViewBase}/v/{SpaceKeys.SpaceIdOf(token)}";
        Assert.Equal(expected, body!["dashboardUrl"]);
        Assert.Equal(expected, response.Headers.GetValues(IngestEndpoints.DashboardUrlHeader).Single());
    }

    [Fact]
    public async Task EventId_repetido_conta_como_duplicado_e_nao_reaplica()
    {
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _client.Send(token, Events.Todo(EventTypes.Created, todoId, 1, "original", eventId));

        // Mesmo eventId com conteúdo diferente (e versão maior): se reaplicasse, o título mudaria.
        var again = await _client.Send(token, Events.Todo(EventTypes.Created, todoId, 2, "adulterado", eventId));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var state = await _client.State(token);
        var todo = Assert.Single(state.Todos);
        Assert.Equal("original", todo.Title);
        Assert.Equal(1, todo.Version);
        Assert.Equal(1, state.Health.Duplicates);
        Assert.Equal("duplicate", state.RecentEvents[0].Result);
    }

    [Fact]
    public async Task Versao_antiga_nao_sobrescreve_a_nova_e_conta_como_desatualizada()
    {
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();
        await _client.Send(token, Events.Todo(EventTypes.Updated, todoId, 5, "versão 5"));

        var late = await _client.Send(token, Events.Todo(EventTypes.Updated, todoId, 4, "versão 4 atrasada"));
        var same = await _client.Send(token, Events.Todo(EventTypes.Snapshot, todoId, 5, "retrato da 5"));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var state = await _client.State(token);
        var todo = Assert.Single(state.Todos);
        Assert.Equal("versão 5", todo.Title);
        Assert.Equal(2, state.Health.Stale);
        Assert.Equal("stale", state.RecentEvents[0].Result);
    }

    [Fact]
    public async Task Versao_nova_substitui_o_estado_inteiro()
    {
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();
        await _client.Send(token, Events.Todo(EventTypes.Created, todoId, 1, "antes"));

        await _client.Send(token, Events.Todo(EventTypes.Updated, todoId, 2, "depois", status: "done"));

        var todo = Assert.Single((await _client.State(token)).Todos);
        Assert.Equal("depois", todo.Title);
        Assert.Equal("done", todo.Status);
    }

    [Fact]
    public async Task Exclusao_marca_a_tarefa_e_tira_dos_contadores_ativos()
    {
        var token = Events.NewToken();
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        await _client.Send(token, Events.Todo(EventTypes.Created, kept, 1));
        await _client.Send(token, Events.Todo(EventTypes.Created, removed, 1));

        var deletedAt = DateTimeOffset.UtcNow;
        var response = await _client.Send(token, Events.Todo(EventTypes.Deleted, removed, 2, deletedAt: deletedAt));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await _client.State(token);
        var todo = state.Todos.Single(t => t.Id == removed);
        Assert.True(todo.Deleted);
        Assert.NotNull(todo.DeletedAt);
        Assert.Equal(1, state.Counters.Total);
        Assert.Equal(1, state.Counters.Deleted);
    }

    [Fact]
    public async Task Evento_de_exclusao_sem_deletedAt_tambem_marca_como_excluida()
    {
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();
        await _client.Send(token, Events.Todo(EventTypes.Created, todoId, 1));

        await _client.Send(token, Events.Todo(EventTypes.Deleted, todoId, 2));

        var todo = Assert.Single((await _client.State(token)).Todos);
        Assert.True(todo.Deleted);
        Assert.NotNull(todo.DeletedAt);
    }

    [Fact]
    public async Task Atualizacao_com_deletedAt_preenchido_tambem_conta_como_exclusao()
    {
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();

        await _client.Send(token, Events.Todo(EventTypes.Updated, todoId, 3, deletedAt: DateTimeOffset.UtcNow));

        Assert.True(Assert.Single((await _client.State(token)).Todos).Deleted);
    }

    [Fact]
    public async Task Contadores_e_saude_refletem_os_eventos()
    {
        var token = Events.NewToken();
        await _client.Send(token, Events.Ping());
        await _client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1, userName: "ana"));
        await _client.Send(token, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1, userName: "bruno", status: "done"));

        var state = await _client.State(token);

        Assert.Equal(3, state.Health.Received);
        Assert.Equal(1, state.Health.EventsByType[EventTypes.Ping]);
        Assert.Equal(2, state.Health.EventsByType[EventTypes.Created]);
        Assert.NotNull(state.Health.LatencyAvgMs);
        Assert.NotNull(state.Health.LastEventAt);
        Assert.Equal(2, state.Counters.Total);
        Assert.Equal(1, state.Counters.ByStatus["pending"]);
        Assert.Equal(1, state.Counters.ByUser["bruno"]);
        Assert.Equal(3, state.RecentEvents.Count);
    }

    [Fact]
    public async Task Ultimos_eventos_guardam_so_os_20_mais_recentes()
    {
        var token = Events.NewToken();
        for (var i = 1; i <= 25; i++)
            await _client.Send(token, Events.Todo(EventTypes.Updated, Guid.NewGuid(), i));

        var state = await _client.State(token);

        Assert.Equal(SpaceLimits.RecentEvents, state.RecentEvents.Count);
        Assert.Equal(25, state.RecentEvents[0].Version);
    }

    [Fact]
    public async Task Tipo_desconhecido_e_aceito_e_ignorado()
    {
        var token = Events.NewToken();
        await _client.Send(token, Events.Ping());

        var response = await _client.Send(token, new { eventId = Guid.NewGuid(), type = "todo.archived", occurredAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await _client.State(token);
        Assert.Equal(1, state.Health.EventsByType[EventTypes.Other]);
        Assert.Equal("ignored", state.RecentEvents[0].Result);
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"eventId":"7d6f2b8e-4f0e-4a59-9a43-2a4c8e0b1f10","type":"todo.created","occurredAt":"2026-01-01T12:00:00Z"}""")]
    [InlineData("""{"eventId":"7d6f2b8e-4f0e-4a59-9a43-2a4c8e0b1f10","type":"todo.created","occurredAt":"2026-01-01T12:00:00Z","todo":{"id":"7d6f2b8e-4f0e-4a59-9a43-2a4c8e0b1f11"}}""")]
    [InlineData("""{"eventId":"nao-e-uuid","type":"webhook.ping","occurredAt":"2026-01-01T12:00:00Z"}""")]
    public async Task Corpo_invalido_recebe_400_e_nao_cria_o_espaco(string body)
    {
        var token = Events.NewToken();

        var response = await _client.PostAsync($"/in/{token}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var state = await _client.GetAsync($"/v/{SpaceKeys.SpaceIdOf(token)}/state");
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
    }

    [Fact]
    public async Task Corpo_acima_de_64_KB_recebe_413()
    {
        var token = Events.NewToken();
        var big = Events.Todo(EventTypes.Created, Guid.NewGuid(), 1, title: new string('x', SpaceLimits.MaxBodyBytes));

        var response = await _client.Send(token, big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Corpo_em_chunks_acima_de_64_KB_tambem_recebe_413()
    {
        var token = Events.NewToken();
        // Sem Content-Length: o limite é contado na leitura.
        var content = new StreamContent(new MemoryStream(new byte[SpaceLimits.MaxBodyBytes + 10]));
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentLength = null;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/in/{token}") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("curto")]
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghij")] // 42 caracteres
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghijkl")] // 44 caracteres
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghi%2B")] // '+' não é base64url
    [InlineData("Way2Do-token-de-teste_0123456789abcdefghi%3D")] // sem padding
    public async Task Token_mal_formado_recebe_404(string token)
    {
        var response = await _client.Send(token, Events.Ping());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Respostas_da_entrada_nao_expoem_o_token()
    {
        var token = Events.NewToken();

        var response = await _client.Send(token, Events.Ping());

        Assert.DoesNotContain(token, response.Headers.ToString() + await response.Content.ReadAsStringAsync());
    }
}
