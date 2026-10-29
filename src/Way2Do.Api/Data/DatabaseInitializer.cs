using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Way2Do.Api.Data;

/// <summary>Aplica as migrations e cria os usuários de teste na subida da aplicação.</summary>
public static class DatabaseInitializer
{
    // Chave arbitrária e fixa do lock consultivo do seed.
    private const long SeedLockKey = 7_250_001;

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        await db.Database.MigrateAsync(ct);
        await SeedUsersAsync(db, config, hasher, clock, logger, ct);
    }

    /// <summary>
    /// Cria cada usuário de <c>Seed:Users:&lt;Nome&gt;:Password</c> que ainda não existe. As senhas vêm do ambiente
    /// (<c>.env</c>), nunca do repositório. Usuário já existente não é alterado.
    /// </summary>
    private static async Task SeedUsersAsync(AppDbContext db, IConfiguration config, IPasswordHasher<User> hasher,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        // Duas instâncias subindo juntas (réplicas, reinício rápido) não podem disputar a criação: o lock de transação
        // do Postgres serializa o seed, e a segunda instância já encontra os usuários criados.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SeedLockKey})", ct);

        foreach (var section in config.GetSection("Seed:Users").GetChildren())
        {
            var userName = section.Key.Trim().ToLowerInvariant();
            var password = section["Password"];
            if (string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("Senha do usuário de teste {UserName} não definida; usuário não criado.", userName);
                continue;
            }

            if (await db.Users.AnyAsync(u => u.UserName == userName, ct))
                continue;

            var user = new User { Id = Guid.NewGuid(), UserName = userName, PasswordHash = "", CreatedAt = clock.GetUtcNow() };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            logger.LogInformation("Usuário de teste {UserName} criado.", userName);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
