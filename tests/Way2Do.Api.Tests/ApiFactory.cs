using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Way2Do.Api.Tests;

/// <summary>Sobe a API em memória contra o Postgres do container, com configuração de teste.</summary>
public sealed class ApiFactory(string connectionString, IDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<Program>
{
    public const string AllowedOrigin = "http://localhost:5300";
    public const string AnaPassword = "senha-da-ana-para-teste";
    public const string BrunoPassword = "senha-do-bruno-para-teste";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Jwt:SigningKey"] = "chave-de-teste-com-pelo-menos-32-bytes!!",
            ["Auth:AllowedOrigins:0"] = AllowedOrigin,
            ["Seed:Users:Ana:Password"] = AnaPassword,
            ["Seed:Users:Bruno:Password"] = BrunoPassword,
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        builder.UseEnvironment(settings.TryGetValue("Environment", out var env) && env is not null ? env : "Testing");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
    }
}
