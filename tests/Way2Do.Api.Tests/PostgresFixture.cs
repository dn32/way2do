using Testcontainers.PostgreSql;

namespace Way2Do.Api.Tests;

/// <summary>
/// Postgres real em container (mesma imagem do Compose) e uma instância da API compartilhada pelos testes que não
/// precisam de configuração própria: o host sobe uma vez, em vez de uma vez por teste.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ApiFactory? _sharedApi;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>API com a configuração padrão de teste e limite de login alto (os testes compartilham o limitador).</summary>
    public ApiFactory SharedApi => _sharedApi ??= new ApiFactory(ConnectionString, new Dictionary<string, string?>
    {
        ["Auth:LoginRateLimit:PermitLimit"] = "1000",
    });

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        if (_sharedApi is not null)
            await _sharedApi.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
