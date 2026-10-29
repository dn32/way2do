using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Way2Do.Api.Data;

/// <summary>
/// Usado só pelo <c>dotnet ef</c> para gerar migrations, sem subir a aplicação (que exige configuração e banco).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=way2do;Username=way2do")
            .Options);
}
