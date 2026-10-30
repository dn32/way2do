using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Way2Do.Api.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Banco e health check. A senha pode vir separada (POSTGRES_PASSWORD do .env), para a connection string não
    /// carregar segredo.
    /// </summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration config)
    {
        var connection = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada."));
        if (string.IsNullOrEmpty(connection.Password) && config["POSTGRES_PASSWORD"] is { Length: > 0 } dbPassword)
            connection.Password = dbPassword;

        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection.ConnectionString));
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
        return services;
    }

    /// <summary>Migrations e usuários de teste na subida (Database:InitializeOnStartup, ligado por padrão).</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
            await DatabaseInitializer.InitializeAsync(app.Services);
    }
}
