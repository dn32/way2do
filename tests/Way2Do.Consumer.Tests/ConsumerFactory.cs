using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Way2Do.Consumer;
using Way2Do.Consumer.Spaces;

namespace Way2Do.Consumer.Tests;

/// <summary>Sobe o consumidor em memória com URLs base de teste (diferentes entre si, para pegar troca).</summary>
public sealed class ConsumerFactory(IDictionary<string, string?>? overrides = null, SpaceLimits? limits = null)
    : WebApplicationFactory<Program>
{
    public const string IngestBase = "http://consumer-teste:8080";
    public const string ViewBase = "http://painel.teste:5200";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Consumer:IngestBaseUrl"] = IngestBase,
            ["Consumer:ViewBaseUrl"] = ViewBase + "/",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        builder.UseEnvironment("Testing");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        if (limits is not null)
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(limits)));
    }
}

/// <summary>Monta eventos no formato do Way2Do.</summary>
public static class Events
{
    public static string NewToken() => SpaceKeys.NewToken();

    public static object Ping(Guid? eventId = null) => new
    {
        eventId = eventId ?? Guid.NewGuid(),
        type = EventTypes.Ping,
        occurredAt = DateTimeOffset.UtcNow,
    };

    public static object Todo(string type, Guid todoId, long version, string title = "Comprar cimento",
        Guid? eventId = null, string userName = "ana", string status = "pending", DateTimeOffset? deletedAt = null) => new
    {
        eventId = eventId ?? Guid.NewGuid(),
        type,
        occurredAt = DateTimeOffset.UtcNow,
        todo = new
        {
            id = todoId,
            userId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            userName,
            version,
            title,
            description = "para a obra",
            dueDate = "2026-11-02",
            priority = "high",
            status,
            tags = new[] { "obra" },
            urgent = false,
            createdAt = DateTimeOffset.UtcNow,
            deletedAt,
        },
    };

    public static Task<HttpResponseMessage> Send(this HttpClient client, string token, object evt) =>
        client.PostAsJsonAsync($"/in/{token}", evt);

    public static async Task<StateDto> State(this HttpClient client, string token) =>
        (await client.GetFromJsonAsync<StateDto>($"/v/{SpaceKeys.SpaceIdOf(token)}/state"))!;
}

// Forma do JSON do painel, só com o que os testes conferem.
public sealed record StateDto(List<TodoDto> Todos, CountersDto Counters, HealthDto Health, FailureDto FailureMode,
    List<RecentDto> RecentEvents);

public sealed record TodoDto(Guid Id, string Title, long Version, bool Deleted, DateTimeOffset? DeletedAt, string Status);

public sealed record CountersDto(int Total, int Deleted, int UrgentPending, Dictionary<string, int> ByStatus,
    Dictionary<string, int> ByUser);

public sealed record HealthDto(long Received, Dictionary<string, long> EventsByType, long Duplicates, long Stale,
    long SnapshotsRequested, long DroppedByLimit, double? LatencyAvgMs, double? LatencyMaxMs, DateTimeOffset? LastEventAt);

public sealed record FailureDto(string Mode, long Refused);

public sealed record RecentDto(string Type, Guid? TodoId, long? Version, string Result);
