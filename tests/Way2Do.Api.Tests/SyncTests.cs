using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Way2Do.Api.Data;
using Way2Do.Api.Sync;

namespace Way2Do.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SyncTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);
    private static readonly DateTimeOffset T3 = T1.AddHours(2);

    // ---------- apoio ----------

    private sealed record Session(HttpClient Client, Guid UserId);

    private async Task<Session> LoginAsync(string user, string password)
    {
        var client = postgres.SharedApi.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.AllowedOrigin);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = user, password });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var body = await login.Content.ReadFromJsonAsync<UserBody>();
        return new Session(client, body!.Id);
    }

    private Task<Session> AnaAsync() => LoginAsync("ana", ApiFactory.AnaPassword);

    private Task<Session> BrunoAsync() => LoginAsync("bruno", ApiFactory.BrunoPassword);

    private static object Create(Guid id, string title, DateTimeOffset ts, object? extraFields = null) => new
    {
        opId = Guid.NewGuid(),
        taskId = id,
        createdAt = ts,
        fields = extraFields ?? new Dictionary<string, object> { ["title"] = new { value = title, ts } },
    };

    private static object Edit(Guid id, string field, object? value, DateTimeOffset ts) => new
    {
        opId = Guid.NewGuid(),
        taskId = id,
        fields = new Dictionary<string, object> { [field] = new { value, ts } },
    };

    private static object Delete(Guid id, DateTimeOffset ts) => new { opId = Guid.NewGuid(), taskId = id, deletedAt = ts };

    private static async Task<PushResponse> PushAsync(Session s, params object[] operations)
    {
        var response = await s.Client.PostAsJsonAsync("/api/sync/push", new { operations });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PushResponse>())!;
    }

    // Para operações malformadas de propósito: o JSON é montado à mão, sem passar por tipos.
    private static JsonObject Op(Guid? taskId, JsonObject rest)
    {
        var op = new JsonObject { ["opId"] = Guid.NewGuid().ToString() };
        if (taskId is not null)
            op["taskId"] = taskId.Value.ToString();
        foreach (var (key, value) in rest.ToList())
        {
            rest.Remove(key);
            op[key] = value;
        }
        return op;
    }

    private static JsonObject Field(string value, string ts) => new() { ["value"] = value, ["ts"] = ts };

    private static Task<HttpResponseMessage> PostRawAsync(Session s, JsonArray operations) =>
        s.Client.PostAsync("/api/sync/push", new StringContent(
            new JsonObject { ["operations"] = operations }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

    private static async Task<PullResponse> PullAsync(Session s, long since, int limit = 1000)
    {
        var response = await s.Client.GetAsync($"/api/sync/pull?since={since}&limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PullResponse>())!;
    }

    // Cursor atual: tudo que já existe no banco compartilhado pelos testes fica para trás.
    private static async Task<long> CurrentCursorAsync(Session s)
    {
        long cursor = 0;
        PullResponse page;
        do
        {
            page = await PullAsync(s, cursor);
            cursor = page.Cursor;
        } while (page.HasMore);
        return cursor;
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = postgres.SharedApi.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ---------- push e pull ----------

    [Fact]
    public async Task Push_cria_a_tarefa_e_o_pull_devolve_com_os_metadados_do_merge()
    {
        var ana = await AnaAsync();
        var cursor = await CurrentCursorAsync(ana);
        var id = Guid.NewGuid();

        var push = await PushAsync(ana, new
        {
            opId = Guid.NewGuid(),
            taskId = id,
            createdAt = T1,
            fields = new Dictionary<string, object>
            {
                ["title"] = new { value = "Urgente: trocar disjuntor", ts = T1 },
                ["priority"] = new { value = "high", ts = T1 },
                ["dueDate"] = new { value = "2026-10-29", ts = T1 },
            },
            tags = new Dictionary<string, object> { ["obra"] = new { present = true, ts = T1 } },
        });
        var pull = await PullAsync(ana, cursor);

        Assert.Equal(PushResult.Accepted, push.Results.Single().Status);
        var task = Assert.Single(pull.Tasks);
        Assert.Equal(id, task.Id);
        Assert.Equal("Urgente: trocar disjuntor", task.Title);
        Assert.Equal("high", task.Priority);
        Assert.Equal(new DateOnly(2026, 10, 29), task.DueDate);
        Assert.Equal(1, task.Version);
        Assert.Equal(T1, task.CreatedAt);
        Assert.Equal(T1, task.FieldTimestamps["title"]);
        Assert.True(task.Tags["obra"].Present);
        Assert.Equal(task.ChangeSeq, pull.Cursor);
        Assert.False(pull.HasMore);
    }

    [Fact]
    public async Task Reenviar_a_mesma_operacao_nao_gera_nova_versao_nem_novo_evento()
    {
        var ana = await AnaAsync();
        var id = Guid.NewGuid();
        var create = Create(id, "Comprar EPI", T1);

        await PushAsync(ana, create);
        var again = await PushAsync(ana, create);

        Assert.Equal(PushResult.Accepted, again.Results.Single().Status);
        var (version, events) = await WithDbAsync(async db => (
            await db.Tasks.Where(t => t.Id == id).Select(t => t.Version).SingleAsync(),
            await db.OutboxEvents.CountAsync(e => e.TaskId == id)));
        Assert.Equal(1, version);
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Cada_alteracao_de_conteudo_gera_versao_evento_e_entrega_por_assinatura()
    {
        var ana = await AnaAsync();
        var subscriptionId = await WithDbAsync(async db =>
        {
            var sub = new WebhookSubscription
            {
                Id = Guid.NewGuid(),
                UserId = ana.UserId,
                Url = $"https://consumidor.example/in/{Guid.NewGuid():N}",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.WebhookSubscriptions.Add(sub);
            await db.SaveChangesAsync();
            return sub.Id;
        });
        try
        {
            var id = Guid.NewGuid();

            await PushAsync(ana, Create(id, "Revisar orçamento", T1));
            await PushAsync(ana, Edit(id, "status", "done", T2));
            await PushAsync(ana, Edit(id, "status", "done", T3)); // mesmo valor, timestamp novo: só metadado
            await PushAsync(ana, Delete(id, T3));

            var events = await WithDbAsync(db => db.OutboxEvents.Where(e => e.TaskId == id)
                .OrderBy(e => e.ChangeSeq).Select(e => new { e.Type, e.TaskVersion, e.Payload }).ToListAsync());
            var deliveries = await WithDbAsync(db => db.WebhookDeliveries
                .Where(d => d.SubscriptionId == subscriptionId).Select(d => d.Status).ToListAsync());

            Assert.Equal(["todo.created", "todo.updated", "todo.deleted"], events.Select(e => e.Type));
            Assert.Equal([1, 2, 3], events.Select(e => e.TaskVersion!.Value));
            using var payload = System.Text.Json.JsonDocument.Parse(events[0].Payload);
            var todo = payload.RootElement.GetProperty("todo");
            Assert.Equal("todo.created", payload.RootElement.GetProperty("type").GetString());
            Assert.Equal("ana", todo.GetProperty("userName").GetString());
            Assert.False(todo.GetProperty("urgent").GetBoolean());
            Assert.Equal(1, todo.GetProperty("version").GetInt32());
            Assert.Equal(3, deliveries.Count);
            Assert.All(deliveries, s => Assert.Equal(DeliveryStatus.Pending, s));
        }
        finally
        {
            // Banco compartilhado pela coleção: a assinatura não pode vazar para os outros testes.
            await WithDbAsync(db => db.WebhookSubscriptions.Where(s => s.Id == subscriptionId).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Varias_operacoes_na_mesma_tarefa_no_lote_geram_uma_versao_e_um_evento()
    {
        var ana = await AnaAsync();
        var id = Guid.NewGuid();

        await PushAsync(ana, Create(id, "A", T1), Edit(id, "title", "B", T2), Edit(id, "priority", "high", T2));

        var (version, events) = await WithDbAsync(async db => (
            await db.Tasks.Where(t => t.Id == id).Select(t => t.Version).SingleAsync(),
            await db.OutboxEvents.Where(e => e.TaskId == id).Select(e => e.Type).ToListAsync()));
        Assert.Equal(1, version);
        Assert.Equal(["todo.created"], events);
    }

    [Fact]
    public async Task Tarefa_criada_e_excluida_no_mesmo_lote_nao_gera_evento()
    {
        var ana = await AnaAsync();
        var id = Guid.NewGuid();

        await PushAsync(ana, Create(id, "Temporária", T1), Delete(id, T2));

        var events = await WithDbAsync(db => db.OutboxEvents.CountAsync(e => e.TaskId == id));
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Operacao_invalida_e_recusada_com_o_estado_do_servidor_e_as_demais_seguem()
    {
        var ana = await AnaAsync();
        var existing = Guid.NewGuid();
        var other = Guid.NewGuid();
        await PushAsync(ana, Create(existing, "Comprar EPI", T1));

        var push = await PushAsync(ana,
            new
            {
                opId = Guid.NewGuid(),
                taskId = existing,
                fields = new Dictionary<string, object>
                {
                    ["title"] = new { value = "Não pode ficar", ts = T2 },
                    ["priority"] = new { value = "urgentissima", ts = T2 },
                },
            },
            Create(other, "Outra tarefa", T2));

        var rejected = push.Results[0];
        Assert.Equal(PushResult.Rejected, rejected.Status);
        Assert.Equal("priority", rejected.Field);
        Assert.Equal("Comprar EPI", rejected.Task!.Title);
        Assert.Equal(PushResult.Accepted, push.Results[1].Status);
        var titles = await WithDbAsync(db => db.Tasks.Where(t => t.Id == existing || t.Id == other)
            .Select(t => t.Title).OrderBy(t => t).ToListAsync());
        Assert.Equal(["Comprar EPI", "Outra tarefa"], titles);
    }

    [Fact]
    public async Task Criacao_invalida_e_recusada_sem_estado_do_servidor()
    {
        var ana = await AnaAsync();
        var id = Guid.NewGuid();

        var push = await PushAsync(ana, Edit(id, "status", "done", T1)); // tarefa nova sem título

        var result = push.Results.Single();
        Assert.Equal(PushResult.Rejected, result.Status);
        Assert.Equal("title", result.Field);
        Assert.Null(result.Task);
        Assert.False(await WithDbAsync(db => db.Tasks.AnyAsync(t => t.Id == id)));
    }

    [Fact]
    public async Task Operacao_sobre_tarefa_de_outro_usuario_e_recusada_sem_revelar_a_tarefa()
    {
        var ana = await AnaAsync();
        var bruno = await BrunoAsync();
        var id = Guid.NewGuid();
        await PushAsync(ana, Create(id, "Tarefa da Ana", T1));

        var push = await PushAsync(bruno, Edit(id, "title", "Invadida", T2));

        var result = push.Results.Single();
        Assert.Equal(PushResult.Rejected, result.Status);
        Assert.Null(result.Task);
        Assert.Equal("Tarefa da Ana", await WithDbAsync(db => db.Tasks.Where(t => t.Id == id).Select(t => t.Title).SingleAsync()));
    }

    [Fact]
    public async Task Pull_so_traz_tarefas_do_proprio_usuario()
    {
        var ana = await AnaAsync();
        var bruno = await BrunoAsync();
        var brunoCursor = await CurrentCursorAsync(bruno);
        var anaTask = Guid.NewGuid();
        var brunoTask = Guid.NewGuid();

        await PushAsync(ana, Create(anaTask, "Da Ana", T1));
        await PushAsync(bruno, Create(brunoTask, "Do Bruno", T1));
        var pull = await PullAsync(bruno, brunoCursor);

        Assert.Contains(pull.Tasks, t => t.Id == brunoTask);
        Assert.DoesNotContain(pull.Tasks, t => t.Id == anaTask);
    }

    [Fact]
    public async Task Pull_paginado_entrega_tudo_em_ordem_e_inclui_excluidas()
    {
        var ana = await AnaAsync();
        var cursor = await CurrentCursorAsync(ana);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        await PushAsync(ana, ids.Select((id, i) => Create(id, $"Tarefa {i}", T1)).ToArray());
        await PushAsync(ana, Delete(ids[0], T2));

        var seen = new List<SyncTask>();
        PullResponse page;
        var pages = 0;
        do
        {
            page = await PullAsync(ana, cursor, limit: 2);
            seen.AddRange(page.Tasks);
            cursor = page.Cursor;
            pages++;
        } while (page.HasMore);

        Assert.Equal(3, pages);
        Assert.Equal(ids.Order(), seen.Select(t => t.Id).Order());
        Assert.Equal(seen.Select(t => t.ChangeSeq).Order(), seen.Select(t => t.ChangeSeq)); // em ordem de change_seq
        Assert.NotNull(seen.Single(t => t.Id == ids[0]).DeletedAt);
    }

    [Fact]
    public async Task Timestamp_no_futuro_e_limitado_ao_horario_do_servidor()
    {
        var ana = await AnaAsync();
        var id = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        await PushAsync(ana, Create(id, "Relógio adiantado", new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var stored = await WithDbAsync(db => db.Tasks.Where(t => t.Id == id).Select(t => t.FieldTimestamps).SingleAsync());
        Assert.InRange(stored["title"], before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Operacoes_malformadas_sao_recusadas_uma_a_uma_sem_derrubar_o_lote()
    {
        var ana = await AnaAsync();
        var good = Guid.NewGuid();
        var operations = new JsonArray(
            null,
            Op(Guid.NewGuid(), new JsonObject { ["fields"] = new JsonObject { ["title"] = null } }),
            Op(Guid.NewGuid(), new JsonObject { ["tags"] = new JsonObject { ["obra"] = null } }),
            Op(Guid.NewGuid(), new JsonObject { ["fields"] = new JsonObject { ["title"] = Field("x", "ontem") } }),
            Op(null, new JsonObject { ["fields"] = new JsonObject { ["title"] = Field("sem taskId", "2026-10-29T10:00:00Z") } }),
            Op(good, new JsonObject
            {
                ["createdAt"] = "2026-10-29T10:00:00Z",
                ["fields"] = new JsonObject { ["title"] = Field("Válida", "2026-10-29T10:00:00Z") },
            }));

        var response = await PostRawAsync(ana, operations);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results;
        Assert.Equal(6, results.Count);
        Assert.All(results.Take(5), r => Assert.Equal(PushResult.Rejected, r.Status));
        Assert.Equal(PushResult.Accepted, results[5].Status);
        Assert.True(await WithDbAsync(db => db.Tasks.AnyAsync(t => t.Id == good)));
    }

    [Fact]
    public async Task Recusa_traz_o_estado_final_do_lote_com_versao_e_change_seq()
    {
        var ana = await AnaAsync();
        var id = Guid.NewGuid();

        var push = await PushAsync(ana,
            Create(id, "Primeiro título", T1),
            Edit(id, "priority", "urgentissima", T2),
            Edit(id, "title", "Título final", T3));

        var rejected = push.Results[1];
        Assert.Equal(PushResult.Rejected, rejected.Status);
        Assert.Equal("Título final", rejected.Task!.Title);
        Assert.Equal(1, rejected.Task.Version);
        Assert.True(rejected.Task.ChangeSeq > 0);
    }

    [Fact]
    public async Task Operacao_malformada_citando_tarefa_de_outro_usuario_nao_vaza_o_estado_dela()
    {
        var ana = await AnaAsync();
        var bruno = await BrunoAsync();
        var anaTask = Guid.NewGuid();
        await PushAsync(ana, Create(anaTask, "Segredo da Ana", T1));

        var operations = new JsonArray(
            Op(anaTask, new JsonObject { ["fields"] = new JsonObject { ["title"] = Field("x", "2026-10-29T10:00:00Z") } }),
            Op(anaTask, new JsonObject { ["fields"] = new JsonObject { ["title"] = null } }));

        var response = await PostRawAsync(bruno, operations);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Segredo da Ana", body);
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var results = System.Text.Json.JsonSerializer.Deserialize<PushResponse>(body, web)!.Results;
        Assert.All(results, r => Assert.Null(r.Task));
    }

    [Fact]
    public async Task Mesmo_id_criado_ao_mesmo_tempo_por_dois_usuarios_nao_derruba_os_lotes()
    {
        var ana = await AnaAsync();
        var bruno = await BrunoAsync();

        for (var round = 0; round < 10; round++)
        {
            var id = Guid.NewGuid();
            var pushes = await Task.WhenAll(
                ana.Client.PostAsJsonAsync("/api/sync/push", new { operations = new[] { Create(id, "Da Ana", T1) } }),
                bruno.Client.PostAsJsonAsync("/api/sync/push", new { operations = new[] { Create(id, "Do Bruno", T1) } }));

            Assert.All(pushes, p => Assert.Equal(HttpStatusCode.OK, p.StatusCode));
            var statuses = new List<string>();
            foreach (var p in pushes)
                statuses.Add((await p.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single().Status);
            Assert.Equal(1, statuses.Count(s => s == PushResult.Accepted));
        }
    }

    [Fact]
    public async Task Lote_vazio_responde_sem_resultados()
    {
        var ana = await AnaAsync();

        var push = await PushAsync(ana);

        Assert.Empty(push.Results);
    }

    [Fact]
    public async Task Corpo_acima_de_1_MB_e_recusado_com_413()
    {
        var ana = await AnaAsync();
        var content = new StringContent(new string(' ', 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");

        var response = await ana.Client.PostAsync("/api/sync/push", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Corpo_que_nao_e_JSON_e_recusado_com_400()
    {
        var ana = await AnaAsync();

        var response = await ana.Client.PostAsync("/api/sync/push", new StringContent("{ operations: [", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- limites e acesso ----------

    [Fact]
    public async Task Lote_acima_de_500_operacoes_e_recusado()
    {
        var ana = await AnaAsync();
        var operations = Enumerable.Range(0, 501).Select(_ => Create(Guid.NewGuid(), "x", T1)).ToArray();

        var response = await ana.Client.PostAsJsonAsync("/api/sync/push", new { operations });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("since=-1")]
    [InlineData("limit=0")]
    [InlineData("limit=1001")]
    public async Task Parametros_invalidos_do_pull_sao_recusados(string query)
    {
        var ana = await AnaAsync();

        var response = await ana.Client.GetAsync($"/api/sync/pull?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sem_sessao_push_e_pull_sao_recusados()
    {
        var client = postgres.SharedApi.CreateClient(new() { HandleCookies = false });

        var push = await client.PostAsJsonAsync("/api/sync/push", new { operations = Array.Empty<object>() });
        var pull = await client.GetAsync("/api/sync/pull");

        Assert.Equal(HttpStatusCode.Unauthorized, push.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, pull.StatusCode);
    }

    // ---------- concorrência ----------

    [Fact]
    public async Task Push_espera_o_lock_do_usuario()
    {
        var ana = await AnaAsync();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@u, 7250002))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("u", ana.UserId.ToString());
            await lockCommand.ExecuteNonQueryAsync();
        }

        var push = PushAsync(ana, Create(Guid.NewGuid(), "Esperando o lock", T1));
        var finishedWhileLocked = await Task.WhenAny(push, Task.Delay(TimeSpan.FromMilliseconds(800))) == push;
        await transaction.CommitAsync(); // libera o lock
        var result = await push.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(finishedWhileLocked, "o push terminou com o lock do usuário preso por outra transação");
        Assert.Equal(PushResult.Accepted, result.Results.Single().Status);
    }

    [Fact]
    public async Task Pulls_simultaneos_a_pushes_paralelos_nao_perdem_alteracoes()
    {
        var ana = await AnaAsync();
        var cursor = await CurrentCursorAsync(ana);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var seen = new HashSet<Guid>();

        var pushes = Task.WhenAll(ids.Select(id => PushAsync(ana, Create(id, "Paralela", T1))));
        while (!pushes.IsCompleted)
        {
            var page = await PullAsync(ana, cursor, limit: 3);
            foreach (var t in page.Tasks)
                seen.Add(t.Id);
            cursor = page.Cursor;
        }
        await pushes;
        PullResponse last;
        do
        {
            last = await PullAsync(ana, cursor);
            foreach (var t in last.Tasks)
                seen.Add(t.Id);
            cursor = last.Cursor;
        } while (last.HasMore);

        Assert.Empty(ids.Except(seen));
    }

    private sealed record UserBody(Guid Id, string Name);
}
