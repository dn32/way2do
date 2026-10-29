using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Way2Do.Api.Data;

namespace Way2Do.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class StartupTests(PostgresFixture postgres)
{
    [Fact]
    public void Em_producao_a_api_nao_sobe_sem_chave_do_jwt()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Environment"] = "Production",
            ["Jwt:SigningKey"] = "",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Jwt:SigningKey é obrigatória no ambiente 'Production'", Flatten(error));
    }

    [Fact]
    public void Ambiente_fora_da_lista_tambem_exige_a_chave_do_jwt()
    {
        // Staging (ou um nome digitado errado) não pode subir calado com chave aleatória.
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Environment"] = "Staging",
            ["Jwt:SigningKey"] = "",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Jwt:SigningKey é obrigatória no ambiente 'Staging'", Flatten(error));
    }

    [Fact]
    public async Task Ambiente_Local_sem_chave_do_jwt_sobe_com_chave_gerada()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Environment"] = "Local",
            ["Jwt:SigningKey"] = "",
        });

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Sem_origens_permitidas_a_api_nao_sobe()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Auth:AllowedOrigins:0"] = "",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Auth:AllowedOrigins precisa de ao menos uma origem", Flatten(error));
    }

    [Fact]
    public async Task Cookie_sem_Secure_quando_desligado_para_http_local()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Auth:SecureCookie"] = "false",
        });
        var client = factory.CreateClient(new() { HandleCookies = false });

        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "ana", password = ApiFactory.AnaPassword });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("way2do-session=", StringComparison.Ordinal));
        var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Add("Cookie", cookie.Split(';')[0]);

        Assert.DoesNotContain("secure", cookie.ToLowerInvariant());
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(me)).StatusCode);
    }

    [Fact]
    public void Chave_do_jwt_curta_demais_impede_a_subida()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "curta",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("no mínimo 32 bytes", Flatten(error));
    }

    [Fact]
    public async Task Fora_de_producao_sem_chave_a_api_gera_uma_e_funciona()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "",
        });
        var client = factory.CreateClient(new() { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "ana", password = ApiFactory.AnaPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_tem_limite_de_tentativas()
    {
        using var factory = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Auth:LoginRateLimit:PermitLimit"] = "3",
            ["Auth:LoginRateLimit:WindowSeconds"] = "60",
        });
        var client = factory.CreateClient(new() { HandleCookies = false });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { userName = "ana", password = "errada" })).StatusCode);

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests],
            statuses);
    }

    [Fact]
    public async Task Seed_nao_cria_usuario_sem_senha_e_nao_duplica_usuarios()
    {
        // Duas subidas seguidas sobre o mesmo banco: a segunda não pode falhar nem duplicar.
        using (var first = new ApiFactory(postgres.ConnectionString))
            first.CreateClient();
        using var second = new ApiFactory(postgres.ConnectionString, new Dictionary<string, string?>
        {
            ["Seed:Users:Carlos:Password"] = "",
        });
        second.CreateClient();

        await using var scope = second.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.Users.Select(u => u.UserName).ToListAsync();

        Assert.Equal(1, names.Count(n => n == "ana"));
        Assert.Equal(1, names.Count(n => n == "bruno"));
        Assert.DoesNotContain("carlos", names);
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (var e = error; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        return string.Join(" | ", messages);
    }
}
