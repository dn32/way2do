using System.Net;
using System.Net.Http.Json;

namespace Way2Do.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthTests(PostgresFixture postgres)
{
    private HttpClient Client() => postgres.SharedApi.CreateClient(new() { HandleCookies = false });

    private static Task<HttpResponseMessage> Login(HttpClient client, string user, string password, string? origin = ApiFactory.AllowedOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userName = user, password }),
        };
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return client.SendAsync(request);
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Secure-way2do-session=", StringComparison.Ordinal));

    [Fact]
    public async Task Login_com_credenciais_validas_devolve_usuario_e_cookie_seguro()
    {
        var response = await Login(Client(), "ana", ApiFactory.AnaPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserBody>();
        Assert.Equal("ana", body!.Name);
        Assert.NotEqual(Guid.Empty, body.Id);

        var cookie = SessionCookie(response).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api", cookie);
        Assert.Contains("expires=", cookie);
    }

    [Fact]
    public async Task Login_ignora_maiusculas_e_espacos_no_usuario()
    {
        var response = await Login(Client(), "  ANA ", ApiFactory.AnaPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Senha_errada_e_usuario_inexistente_recebem_o_mesmo_erro_generico()
    {
        var wrongPassword = await Login(Client(), "ana", "senha-errada");
        var unknownUser = await Login(Client(), "carlos", "qualquer");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        var a = await wrongPassword.Content.ReadFromJsonAsync<ProblemBody>();
        var b = await unknownUser.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Usuário ou senha inválidos.", a!.Title);
        Assert.Equal(a.Title, b!.Title);
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Origin_nao_permitido_e_recusado_antes_do_login()
    {
        var response = await Login(Client(), "ana", ApiFactory.AnaPassword, origin: "https://site-malicioso.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Requisicao_sem_Origin_segue()
    {
        var response = await Login(Client(), "ana", ApiFactory.AnaPassword, origin: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_da_sessao_autentica_e_sem_ele_a_rota_protegida_recusa()
    {
        var client = Client();
        var login = await Login(client, "bruno", ApiFactory.BrunoPassword);
        var cookie = SessionCookie(login).Split(';')[0];

        var anonymous = await client.GetAsync("/api/auth/me");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        var authenticated = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal("bruno", (await authenticated.Content.ReadFromJsonAsync<UserBody>())!.Name);
    }

    [Fact]
    public async Task Token_no_cabecalho_Authorization_nao_e_aceito()
    {
        var client = Client();
        var login = await Login(client, "ana", ApiFactory.AnaPassword);
        var token = SessionCookie(login).Split(';')[0].Split('=', 2)[1];

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_adulterado_e_recusado()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", "__Secure-way2do-session=eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0.");

        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_apaga_o_cookie_mesmo_sem_sessao()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Origin", ApiFactory.AllowedOrigin);

        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = SessionCookie(response).ToLowerInvariant();
        Assert.Contains("expires=thu, 01 jan 1970", cookie);
        Assert.Contains("path=/api", cookie);
    }

    [Fact]
    public async Task Respostas_da_api_nao_vao_para_cache()
    {
        var response = await Login(Client(), "ana", ApiFactory.AnaPassword);

        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Health_confere_o_banco()
    {
        var response = await Client().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    private sealed record UserBody(Guid Id, string Name);

    private sealed record ProblemBody(string Title, int Status);
}
