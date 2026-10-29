using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Way2Do.Api.Data;
using Way2Do.Api.Sync;
using Way2Do.Api.Webhooks;

namespace Way2Do.Api.Tests;

/// <summary>
/// Consumidor de teste de verdade (Kestrel em 127.0.0.1, porta livre): registra o que recebe e responde o que o teste
/// mandar. Por padrão, 200.
/// </summary>
public sealed class TestConsumer : IAsyncDisposable
{
    public sealed record Received(string Path, string EventId, string EventType, JsonElement Body);

    private readonly WebApplication _app;

    public ConcurrentQueue<Received> Requests { get; } = new();

    /// <summary>Resposta para cada requisição recebida: status, corpo e espera antes de responder.</summary>
    public Func<Received, (int Status, string? Body, TimeSpan Delay)> Respond { get; set; } = _ => (200, null, TimeSpan.Zero);

    /// <summary>Cabeçalhos acrescentados a toda resposta.</summary>
    public Dictionary<string, string> ResponseHeaders { get; } = [];

    public string BaseUrl { get; }

    /// <summary>Status especial: o consumidor manda os cabeçalhos e derruba a conexão no meio do corpo.</summary>
    public const int DropConnection = -1;

    private TestConsumer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<TestConsumer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        TestConsumer? consumer = null;
        app.MapPost("/{**path}", async (HttpContext http) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
            var received = new Received(http.Request.Path, http.Request.Headers["X-Way2Do-Event-Id"].ToString(),
                http.Request.Headers["X-Way2Do-Event-Type"].ToString(), body);
            consumer!.Requests.Enqueue(received);
            var (status, responseBody, delay) = consumer.Respond(received);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            if (status == DropConnection)
            {
                // Cabeçalhos e um pedaço do corpo, e a conexão cai.
                http.Response.ContentLength = 1000;
                await http.Response.WriteAsync("{\"parcial\":");
                await http.Response.Body.FlushAsync();
                http.Abort();
                return;
            }
            http.Response.StatusCode = status;
            foreach (var (name, value) in consumer.ResponseHeaders)
                http.Response.Headers[name] = value;
            if (status is >= 300 and < 400)
                http.Response.Headers.Location = "http://169.254.169.254/latest/meta-data/";
            if (responseBody is not null)
            {
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(responseBody);
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        consumer = new TestConsumer(app, address);
        return consumer;
    }

    public string Url(string token = "token-de-teste") => $"{BaseUrl}/in/{token}";

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class WebhookTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    private ApiFactory _api = null!;
    private TestConsumer _consumer = null!;

    // API própria: rede local liberada (o consumidor de teste está em 127.0.0.1) e limite de cadastro alto.
    public async Task InitializeAsync()
    {
        _api = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Webhooks:AllowPrivateNetworks"] = "true",
            ["Webhooks:RegisterRateLimit:PermitLimit"] = "1000",
            ["Auth:LoginRateLimit:PermitLimit"] = "1000",
        });
        _consumer = await TestConsumer.StartAsync();
        // Cada teste começa sem assinaturas nem entregas (a API e o banco são compartilhados entre testes).
        await WithDbAsync(async db =>
        {
            await db.WebhookSubscriptions.ExecuteDeleteAsync();
            await db.OutboxEvents.ExecuteDeleteAsync();
            return 0;
        });
    }

    public async Task DisposeAsync()
    {
        await _consumer.DisposeAsync();
        await _api.DisposeAsync();
    }

    // ---------- apoio ----------

    private sealed record Session(HttpClient Client, Guid UserId, string Name);

    // Usuário novo por teste: o banco é compartilhado e o retrato completo leva TODAS as tarefas do dono.
    private async Task<Session> NewUserAsync(ApiFactory? api = null)
    {
        const string password = "senha-de-teste-123";
        var name = $"teste-{Guid.NewGuid():N}"[..24];
        await using (var scope = (api ?? _api).Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            var user = new User { Id = Guid.NewGuid(), UserName = name, PasswordHash = "", CreatedAt = DateTimeOffset.UtcNow };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        return await LoginAsync(name, password, api);
    }

    private async Task<Session> LoginAsync(string user, string password, ApiFactory? api = null)
    {
        var client = (api ?? _api).CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.AllowedOrigin);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = user, password });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        return new Session(client, body.GetProperty("id").GetGuid(), user);
    }

    private static Task<HttpResponseMessage> RegisterAsync(Session s, string url) =>
        s.Client.PostAsJsonAsync("/api/integrations", new { url });

    private static async Task<IntegrationResponse> RegisterOkAsync(Session s, string url)
    {
        var response = await RegisterAsync(s, url);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IntegrationResponse>())!;
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

    private static async Task<Guid> CreateTaskAsync(Session s, string title)
    {
        var id = Guid.NewGuid();
        var push = await s.Client.PostAsJsonAsync("/api/sync/push", new
        {
            operations = new[]
            {
                new { opId = Guid.NewGuid(), taskId = id, createdAt = T1, fields = new Dictionary<string, object> { ["title"] = new { value = title, ts = T1 } } },
            },
        });
        push.EnsureSuccessStatusCode();
        return id;
    }

    private WebhookDispatcher Dispatcher => _api.Services.GetRequiredService<WebhookDispatcher>();

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<WebhookSubscription> SubscriptionAsync(Guid id) =>
        WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id));

    // ---------- cadastro ----------

    [Fact]
    public async Task Cadastro_faz_o_ping_grava_o_painel_e_devolve_a_url_mascarada()
    {
        var ana = await NewUserAsync();
        _consumer.Respond = _ => (200, """{"dashboardUrl":"http://localhost:5200/v/abc"}""", TimeSpan.Zero);

        var created = await RegisterOkAsync(ana, _consumer.Url("segredo-que-nao-pode-vazar-1234"));

        var ping = Assert.Single(_consumer.Requests);
        Assert.Equal("webhook.ping", ping.EventType);
        Assert.Equal("webhook.ping", ping.Body.GetProperty("type").GetString());
        Assert.False(ping.Body.TryGetProperty("todo", out _));
        Assert.Equal("http://localhost:5200/v/abc", created.DashboardUrl);
        Assert.EndsWith("/…1234", created.Url);
        var list = await ana.Client.GetStringAsync("/api/integrations");
        Assert.DoesNotContain("segredo-que-nao-pode-vazar", list);
        Assert.Contains("http://localhost:5200/v/abc", list);
    }

    [Theory]
    [InlineData(500, "respondeu 500")]
    [InlineData(404, "respondeu 404")]
    [InlineData(302, "redirecionamento")]
    public async Task Ping_sem_2xx_recusa_o_cadastro_com_o_motivo(int status, string reason)
    {
        var ana = await NewUserAsync();
        _consumer.Respond = _ => (status, null, TimeSpan.Zero);

        var response = await RegisterAsync(ana, _consumer.Url());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await DetailAsync(response));
        Assert.Single(_consumer.Requests); // o redirecionamento não foi seguido
        Assert.Equal(0, await WithDbAsync(db => db.WebhookSubscriptions.CountAsync()));
    }

    [Fact]
    public async Task Ping_sem_resposta_em_5_segundos_recusa_o_cadastro()
    {
        var ana = await NewUserAsync();
        _consumer.Respond = _ => (200, null, TimeSpan.FromSeconds(7));

        var response = await RegisterAsync(ana, _consumer.Url());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("não respondeu em 5 s", await DetailAsync(response));
    }

    [Fact]
    public async Task Link_do_painel_vem_do_cabecalho_no_205_que_nao_tem_corpo()
    {
        var ana = await NewUserAsync();
        _consumer.Respond = _ => (205, null, TimeSpan.Zero);
        _consumer.ResponseHeaders[WebhookResponse.DashboardHeader] = "http://localhost:5200/v/do-cabecalho";

        var created = await RegisterOkAsync(ana, _consumer.Url());

        Assert.Equal("http://localhost:5200/v/do-cabecalho", created.DashboardUrl);
    }

    [Fact]
    public async Task Link_do_painel_no_cabecalho_tambem_so_aceita_http()
    {
        var ana = await NewUserAsync();
        _consumer.ResponseHeaders[WebhookResponse.DashboardHeader] = "javascript:alert(1)";

        Assert.Null((await RegisterOkAsync(ana, _consumer.Url())).DashboardUrl);
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html,oi")]
    public async Task Link_do_painel_que_nao_e_http_e_descartado(string dashboard)
    {
        var ana = await NewUserAsync();
        _consumer.Respond = _ => (200, JsonSerializer.Serialize(new { dashboardUrl = dashboard }), TimeSpan.Zero);

        var created = await RegisterOkAsync(ana, _consumer.Url());

        Assert.Null(created.DashboardUrl);
    }

    [Fact]
    public async Task Rede_interna_e_recusada_sem_nenhuma_requisicao_quando_a_liberacao_local_esta_desligada()
    {
        await using var api = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Auth:LoginRateLimit:PermitLimit"] = "1000",
        });
        var ana = await NewUserAsync(api);

        var response = await RegisterAsync(ana, _consumer.Url());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rede interna", await DetailAsync(response));
        Assert.Empty(_consumer.Requests);
    }

    [Fact]
    public async Task Url_repetida_e_limite_de_10_por_usuario()
    {
        var ana = await NewUserAsync();
        for (var i = 0; i < IntegrationEndpoints.MaxSubscriptionsPerUser; i++)
            await RegisterOkAsync(ana, _consumer.Url($"token-{i}"));

        var repeated = await RegisterAsync(ana, _consumer.Url("token-0"));
        var eleventh = await RegisterAsync(ana, _consumer.Url("token-10"));

        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, eleventh.StatusCode);
        Assert.Contains("Limite de 10", await DetailAsync(eleventh));
    }

    [Fact]
    public async Task Cadastro_tem_limite_de_tentativas_por_usuario()
    {
        await using var api = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Webhooks:AllowPrivateNetworks"] = "true",
            ["Webhooks:RegisterRateLimit:PermitLimit"] = "2",
            ["Auth:LoginRateLimit:PermitLimit"] = "1000",
        });
        var ana = await NewUserAsync(api);
        _consumer.Respond = _ => (500, null, TimeSpan.Zero);

        await RegisterAsync(ana, _consumer.Url("a"));
        await RegisterAsync(ana, _consumer.Url("b"));
        var third = await RegisterAsync(ana, _consumer.Url("c"));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(2, _consumer.Requests.Count);
    }

    [Fact]
    public async Task Cada_usuario_ve_e_remove_so_as_proprias_integracoes()
    {
        var ana = await NewUserAsync();
        var bruno = await NewUserAsync();
        var created = await RegisterOkAsync(ana, _consumer.Url());

        Assert.Equal("[]", await bruno.Client.GetStringAsync("/api/integrations"));
        Assert.Equal(HttpStatusCode.NotFound, (await bruno.Client.DeleteAsync($"/api/integrations/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/integrations/{created.Id}")).StatusCode);
        Assert.Equal("[]", await ana.Client.GetStringAsync("/api/integrations"));
    }

    // ---------- entregas ----------

    [Fact]
    public async Task Alteracao_e_entregue_com_o_estado_completo_e_os_cabecalhos()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        var id = await CreateTaskAsync(ana, "Urgente: trocar disjuntor");

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var evt = _consumer.Requests.Last();
        Assert.Equal("todo.created", evt.EventType);
        Assert.Equal(evt.Body.GetProperty("eventId").GetString(), evt.EventId);
        var todo = evt.Body.GetProperty("todo");
        Assert.Equal(id, todo.GetProperty("id").GetGuid());
        Assert.Equal(ana.Name, todo.GetProperty("userName").GetString());
        Assert.True(todo.GetProperty("urgent").GetBoolean());
        Assert.Equal(DeliveryStatus.Delivered, await WithDbAsync(db => db.WebhookDeliveries.Select(d => d.Status).SingleAsync()));
    }

    [Fact]
    public async Task Falha_reagenda_com_backoff_e_as_demais_da_assinatura_esperam_junto()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        _consumer.Respond = _ => (503, null, TimeSpan.Zero);
        await CreateTaskAsync(ana, "Primeira");
        await CreateTaskAsync(ana, "Segunda");
        var before = DateTimeOffset.UtcNow;

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, _consumer.Requests.Count); // ping + uma tentativa só (a segunda nem foi enviada)
        var deliveries = await WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().ToListAsync());
        Assert.All(deliveries, d => Assert.Equal(DeliveryStatus.Pending, d.Status));
        Assert.Equal([0, 1], deliveries.Select(d => d.Attempts).Order());
        Assert.All(deliveries, d => Assert.True(d.NextAttemptAt >= before.AddMilliseconds(400)));
        Assert.Contains("respondeu 503", deliveries.Single(d => d.Attempts == 1).LastError);

        // O consumidor volta: na próxima rodada (depois do backoff) tudo é entregue.
        _consumer.Respond = _ => (200, null, TimeSpan.Zero);
        await WithDbAsync(db => db.WebhookDeliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow)));
        await Dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.All(await WithDbAsync(db => db.WebhookDeliveries.Select(d => d.Status).ToListAsync()), s => Assert.Equal(DeliveryStatus.Delivered, s));
    }

    [Fact]
    public async Task Depois_de_24_horas_a_entrega_desiste()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        await CreateTaskAsync(ana, "Antiga");
        await WithDbAsync(db => db.WebhookDeliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.CreatedAt, DateTimeOffset.UtcNow.AddHours(-25))));
        _consumer.Respond = _ => (500, null, TimeSpan.Zero);

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DeliveryStatus.Failed, await WithDbAsync(db => db.WebhookDeliveries.Select(d => d.Status).SingleAsync()));
    }

    [Fact]
    public async Task Ping_205_envia_o_retrato_so_das_tarefas_do_dono_e_nao_excluidas()
    {
        var ana = await NewUserAsync();
        var bruno = await NewUserAsync();
        var a1 = await CreateTaskAsync(ana, "Da ana 1");
        var a2 = await CreateTaskAsync(ana, "Da ana 2");
        await CreateTaskAsync(bruno, "Do bruno");
        var deleted = await CreateTaskAsync(ana, "Excluída");
        await ana.Client.PostAsJsonAsync("/api/sync/push", new { operations = new[] { new { opId = Guid.NewGuid(), taskId = deleted, deletedAt = T1.AddMinutes(1) } } });
        _consumer.Respond = r => (r.EventType == "webhook.ping" ? 205 : 200, null, TimeSpan.Zero);

        var created = await RegisterOkAsync(ana, _consumer.Url());
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var snapshot = _consumer.Requests.Where(r => r.EventType == "todo.snapshot").Select(r => r.Body.GetProperty("todo").GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { a1, a2 }.Order(), snapshot.Order());
        var subscription = await SubscriptionAsync(created.Id);
        Assert.NotNull(subscription.LastResyncAt);
        Assert.Null(subscription.ResyncDueAt);
    }

    [Fact]
    public async Task Pedidos_de_retrato_no_mesmo_minuto_ficam_agendados_e_agrupados()
    {
        var ana = await NewUserAsync();
        await CreateTaskAsync(ana, "Uma");
        _consumer.Respond = r => (r.EventType == "todo.snapshot" ? 200 : 205, null, TimeSpan.Zero);
        var created = await RegisterOkAsync(ana, _consumer.Url());
        await Dispatcher.RunOnceAsync(CancellationToken.None); // retrato do cadastro
        var first = await SubscriptionAsync(created.Id);

        // Duas alterações respondidas com 205 dentro do minuto: um único retrato, agendado para o fim do intervalo.
        await CreateTaskAsync(ana, "Duas");
        await Dispatcher.RunOnceAsync(CancellationToken.None);
        await CreateTaskAsync(ana, "Três");
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var scheduled = await SubscriptionAsync(created.Id);
        Assert.Equal(first.LastResyncAt + WebhookDispatcher.ResyncInterval, scheduled.ResyncDueAt);
        Assert.Single(_consumer.Requests, r => r.EventType == "todo.snapshot");
    }

    [Fact]
    public async Task Retrato_respondido_com_205_nao_pede_outro_retrato()
    {
        var ana = await NewUserAsync();
        await CreateTaskAsync(ana, "Uma");
        _consumer.Respond = _ => (205, null, TimeSpan.Zero);
        var created = await RegisterOkAsync(ana, _consumer.Url());

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Single(_consumer.Requests, r => r.EventType == "todo.snapshot");
        Assert.Null((await SubscriptionAsync(created.Id)).ResyncDueAt);
    }

    [Fact]
    public async Task Remover_a_assinatura_cancela_as_entregas_pendentes()
    {
        var ana = await NewUserAsync();
        var created = await RegisterOkAsync(ana, _consumer.Url());
        await CreateTaskAsync(ana, "Pendente");

        await ana.Client.DeleteAsync($"/api/integrations/{created.Id}");
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await WithDbAsync(db => db.WebhookDeliveries.CountAsync()));
        Assert.Single(_consumer.Requests); // só o ping
    }

    [Fact]
    public async Task Duas_instancias_ao_mesmo_tempo_nao_entregam_a_mesma_alteracao_duas_vezes()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        for (var i = 0; i < 20; i++)
            await CreateTaskAsync(ana, $"Tarefa {i}");
        _consumer.Respond = _ => (200, null, TimeSpan.FromMilliseconds(20));
        await using var other = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Webhooks:AllowPrivateNetworks"] = "true",
        });
        var otherDispatcher = other.Services.GetRequiredService<WebhookDispatcher>();

        await Task.WhenAll(Dispatcher.RunOnceAsync(CancellationToken.None), otherDispatcher.RunOnceAsync(CancellationToken.None));

        var events = _consumer.Requests.Where(r => r.EventType == "todo.created").Select(r => r.EventId).ToList();
        Assert.Equal(20, events.Count);
        Assert.Equal(20, events.Distinct().Count());
    }

    [Fact]
    public async Task Assinatura_removida_no_meio_do_envio_nao_recebe_mais_nada()
    {
        var ana = await NewUserAsync();
        var created = await RegisterOkAsync(ana, _consumer.Url());
        for (var i = 0; i < 5; i++)
            await CreateTaskAsync(ana, $"Tarefa {i}");
        // Na primeira entrega, o usuário remove a integração.
        _consumer.Respond = _ =>
        {
            WithDbAsync(db => db.WebhookSubscriptions.Where(s => s.Id == created.Id).ExecuteDeleteAsync()).GetAwaiter().GetResult();
            return (200, null, TimeSpan.Zero);
        };

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Single(_consumer.Requests, r => r.EventType == "todo.created");
    }

    [Fact]
    public async Task Entrega_desistida_tambem_poe_o_resto_do_lote_para_esperar()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        for (var i = 0; i < 3; i++)
            await CreateTaskAsync(ana, $"Antiga {i}");
        await WithDbAsync(db => db.WebhookDeliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.CreatedAt, DateTimeOffset.UtcNow.AddHours(-25))));
        _consumer.Respond = _ => (500, null, TimeSpan.Zero);

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Single(_consumer.Requests, r => r.EventType == "todo.created");
        var deliveries = await WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().ToListAsync());
        Assert.Single(deliveries, d => d.Status == DeliveryStatus.Failed);
        Assert.Equal(2, deliveries.Count(d => d.Status == DeliveryStatus.Pending && d.Attempts == 0 && d.NextAttemptAt > DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Conexao_que_cai_no_meio_da_resposta_e_falha_com_nova_tentativa()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        await CreateTaskAsync(ana, "Conexão instável");
        _consumer.Respond = _ => (TestConsumer.DropConnection, null, TimeSpan.Zero);

        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var delivery = await WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().SingleAsync());
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.Attempts);
        Assert.NotNull(delivery.LastError);
    }

    [Fact]
    public async Task Consumidor_lento_nao_segura_a_entrega_dos_outros()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url("lento"));
        await RegisterOkAsync(ana, _consumer.Url("rapido"));
        _consumer.Respond = r => (200, null, r.Path.EndsWith("lento") && r.EventType != "webhook.ping" ? TimeSpan.FromSeconds(4) : TimeSpan.Zero);
        await CreateTaskAsync(ana, "Para os dois");

        var started = DateTime.UtcNow;
        await Dispatcher.RunOnceAsync(CancellationToken.None, waitForSends: false);
        while (!_consumer.Requests.Any(r => r.Path.EndsWith("rapido") && r.EventType == "todo.created") && DateTime.UtcNow - started < TimeSpan.FromSeconds(3))
            await Task.Delay(20);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "o consumidor rápido esperou o lento");
        // Enquanto o lento não termina, a assinatura dele fica fora da próxima reserva (sem envio em dobro).
        Assert.Equal(0, await Dispatcher.RunOnceAsync(CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(4.5));
        Assert.Single(_consumer.Requests, r => r.Path.EndsWith("lento") && r.EventType == "todo.created");
    }

    [Fact]
    public async Task Push_acorda_o_worker()
    {
        var ana = await NewUserAsync();
        await RegisterOkAsync(ana, _consumer.Url());
        var signal = _api.Services.GetRequiredService<WebhookSignal>();
        while (await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None)) { } // zera sinais anteriores

        await CreateTaskAsync(ana, "Nova");

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}
