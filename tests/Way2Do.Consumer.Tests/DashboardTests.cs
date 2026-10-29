using System.Net;
using System.Net.Http.Json;
using System.Text;
using Way2Do.Consumer.Spaces;

namespace Way2Do.Consumer.Tests;

public sealed class DashboardTests(SharedConsumer consumer) : IClassFixture<SharedConsumer>
{
    private readonly HttpClient _client = consumer.Client();

    private Task<HttpResponseMessage> SetMode(string token, string mode) =>
        _client.PostAsJsonAsync($"/v/{SpaceKeys.SpaceIdOf(token)}/failure-mode", new { mode });

    [Fact]
    public async Task Gerar_nova_URL_devolve_par_coerente_sem_criar_espaco()
    {
        var response = await _client.PostAsync("/new", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pair = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        var ingest = pair["ingestUrl"];
        var view = pair["viewUrl"];
        Assert.StartsWith($"{ConsumerFactory.IngestBase}/in/", ingest);
        Assert.StartsWith($"{ConsumerFactory.ViewBase}/v/", view);
        var token = ingest[(ingest.LastIndexOf('/') + 1)..];
        Assert.True(SpaceKeys.IsValidToken(token));
        Assert.Equal(SpaceKeys.SpaceIdOf(token), view[(view.LastIndexOf('/') + 1)..]);

        // Não grava nada: o espaço só nasce com o primeiro evento.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/v/{SpaceKeys.SpaceIdOf(token)}/state")).StatusCode);
    }

    [Fact]
    public async Task Cada_chamada_gera_um_token_diferente()
    {
        var a = await (await _client.PostAsync("/new", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var b = await (await _client.PostAsync("/new", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.NotEqual(a!["ingestUrl"], b!["ingestUrl"]);
    }

    [Fact]
    public async Task Estado_de_espaco_inexistente_recebe_404_mas_a_pagina_abre()
    {
        var spaceId = SpaceKeys.SpaceIdOf(Events.NewToken());

        var state = await _client.GetAsync($"/v/{spaceId}/state");
        var page = await _client.GetAsync($"/v/{spaceId}");

        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("806C4B3F3FAD6C58384D9329ECD44F6A221D95C0E51B2C73D4A00CF156F7319B")] // maiúsculas
    [InlineData("806c4b3f3fad6c58384d9329ecd44f6a221d95c0e51b2c73d4a00cf156f7319")] // 63
    [InlineData("806c4b3f3fad6c58384d9329ecd44f6a221d95c0e51b2c73d4a00cf156f7319bb")] // 65
    [InlineData("g06c4b3f3fad6c58384d9329ecd44f6a221d95c0e51b2c73d4a00cf156f7319b")]
    public async Task Hash_mal_formado_recebe_404_em_todas_as_rotas_do_painel(string spaceId)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/v/{spaceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/v/{spaceId}/state")).StatusCode);
        var mode = await _client.PostAsJsonAsync($"/v/{spaceId}/failure-mode", new { mode = "down" });
        Assert.Equal(HttpStatusCode.NotFound, mode.StatusCode);
    }

    [Fact]
    public async Task Estado_de_um_espaco_nao_mostra_dados_de_outro()
    {
        var mine = Events.NewToken();
        var other = Events.NewToken();
        await _client.Send(mine, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1, "minha"));
        await _client.Send(other, Events.Todo(EventTypes.Created, Guid.NewGuid(), 1, "alheia"));

        var state = await _client.State(mine);

        var todo = Assert.Single(state.Todos);
        Assert.Equal("minha", todo.Title);
        Assert.Equal(1, state.Health.Received);
        Assert.Single(state.RecentEvents);
    }

    [Fact]
    public async Task Modo_fora_do_ar_recusa_com_503_conta_e_desligar_volta_ao_normal()
    {
        var token = Events.NewToken();
        var todoId = Guid.NewGuid();
        await _client.Send(token, Events.Ping());

        var set = await SetMode(token, "down");
        var refused1 = await _client.Send(token, Events.Todo(EventTypes.Created, todoId, 1));
        var refused2 = await _client.Send(token, Events.Ping());
        var during = await _client.State(token);
        var off = await SetMode(token, "off");
        var accepted = await _client.Send(token, Events.Todo(EventTypes.Created, todoId, 1));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused1.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused2.StatusCode);
        Assert.Equal(new FailureDto("down", 2), during.FailureMode);
        Assert.Empty(during.Todos);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = await _client.State(token);
        Assert.Equal(new FailureDto("off", 2), after.FailureMode);
        Assert.Single(after.Todos);
    }

    [Fact]
    public async Task Modo_instavel_processa_mas_perde_parte_das_confirmacoes_e_o_reenvio_vira_duplicado()
    {
        var token = Events.NewToken();
        await _client.Send(token, Events.Ping());
        await SetMode(token, "flaky");

        var refused = 0;
        var resent = 0;
        for (var i = 0; i < 60; i++)
        {
            var evt = Events.Todo(EventTypes.Created, Guid.NewGuid(), 1, $"tarefa {i}");
            if ((await _client.Send(token, evt)).StatusCode != HttpStatusCode.ServiceUnavailable)
                continue;
            refused++;
            // Como o Way2Do faria: reenvia o mesmo evento até ser confirmado.
            HttpStatusCode again;
            do
            {
                resent++;
                again = (await _client.Send(token, evt)).StatusCode;
                if (again == HttpStatusCode.ServiceUnavailable)
                    refused++;
            } while (again == HttpStatusCode.ServiceUnavailable);
        }

        // Probabilidade de 60 sorteios caírem todos do mesmo lado: 2 × 2^-60.
        Assert.InRange(refused, 1, 1000);
        var state = await _client.State(token);
        Assert.Equal(60, state.Counters.Total); // toda tarefa foi aplicada uma única vez
        Assert.Equal(resent, state.Health.Duplicates);
        Assert.Equal(refused, state.FailureMode.Refused);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "mode=down")]
    [InlineData("text/plain", """{"mode":"down"}""")]
    [InlineData("multipart/form-data; boundary=x", "--x--")]
    public async Task Modo_de_falha_exige_JSON(string contentType, string body)
    {
        var token = Events.NewToken();
        await _client.Send(token, Events.Ping());
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        var response = await _client.PostAsync($"/v/{SpaceKeys.SpaceIdOf(token)}/failure-mode", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("off", (await _client.State(token)).FailureMode.Mode);
    }

    [Theory]
    [InlineData("sempre")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Modo_de_falha_desconhecido_recebe_400(string? mode)
    {
        var token = Events.NewToken();
        await _client.Send(token, Events.Ping());

        var response = await SetMode(token, mode!);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Modo_de_falha_em_espaco_inexistente_recebe_404()
    {
        var response = await SetMode(Events.NewToken(), "down");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/way2do.css")]
    [InlineData("/consumer.css")]
    [InlineData("/dashboard.js")]
    [InlineData("/home.js")]
    [InlineData("/favicon.svg")]
    public async Task Paginas_e_arquivos_estaticos_sao_servidos(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CSS_da_marca_e_o_de_docs_design()
    {
        var css = await _client.GetStringAsync("/way2do.css");

        Assert.Equal("text/css", (await _client.GetAsync("/way2do.css")).Content.Headers.ContentType!.MediaType);
        Assert.Contains("--w2-primary:#2563EB", css);
    }
}
