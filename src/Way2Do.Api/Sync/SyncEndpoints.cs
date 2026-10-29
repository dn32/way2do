using System.Security.Claims;
using Way2Do.Api.Auth;

namespace Way2Do.Api.Sync;

public static class SyncEndpoints
{
    /// <summary>Corpo máximo do push (até 500 operações). O Kestrel aplica o mesmo limite a toda a API.</summary>
    public const long MaxPushBodyBytes = 1024 * 1024;

    /// <summary>
    /// Id do usuário dono do banco local que o aparelho está sincronizando. Opcional; quando vem e não é o usuário
    /// do cookie (ex.: outra aba trocou de usuário no meio de um ciclo), a requisição é recusada com 409 para que a
    /// fila de um usuário nunca seja gravada na conta de outro.
    /// </summary>
    public const string ExpectedUserHeader = "X-Way2Do-User";

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").RequireAuthorization();
        group.MapPost("/push", Push);
        group.MapGet("/pull", Pull);
    }

    private static async Task<IResult> Push(HttpRequest http, ClaimsPrincipal principal, SyncService sync, CancellationToken ct)
    {
        // Recusa explícita (e em ProblemDetails) quando o tamanho é declarado; corpo sem Content-Length é limitado
        // pelo Kestrel.
        if (http.ContentLength > MaxPushBodyBytes)
            return Results.Problem(title: "Lote acima de 1 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);

        PushRequest? request;
        try
        {
            request = await http.ReadFromJsonAsync<PushRequest>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Problem(title: "Corpo do push não é um JSON válido.", statusCode: StatusCodes.Status400BadRequest);
        }

        var operations = request?.Operations ?? [];
        if (operations.Count > SyncService.MaxOperationsPerPush)
            return Results.Problem(
                title: $"No máximo {SyncService.MaxOperationsPerPush} operações por lote.",
                statusCode: StatusCodes.Status400BadRequest);

        if (principal.GetUser() is not { } user)
            return Results.Unauthorized();
        if (OtherUser(http, user.Id) is { } conflict)
            return conflict;
        return Results.Ok(await sync.PushAsync(user.Id, user.Name, operations, ct));
    }

    private static async Task<IResult> Pull(HttpRequest http, ClaimsPrincipal principal, SyncService sync, CancellationToken ct,
        long since = 0, int limit = SyncService.DefaultPullLimit)
    {
        if (since < 0 || limit < 1 || limit > SyncService.MaxPullLimit)
            return Results.Problem(
                title: $"since >= 0 e limit entre 1 e {SyncService.MaxPullLimit}.",
                statusCode: StatusCodes.Status400BadRequest);

        if (principal.GetUser() is not { } user)
            return Results.Unauthorized();
        if (OtherUser(http, user.Id) is { } conflict)
            return conflict;
        return Results.Ok(await sync.PullAsync(user.Id, since, limit, ct));
    }

    private static IResult? OtherUser(HttpRequest http, Guid userId)
    {
        if (!http.Headers.TryGetValue(ExpectedUserHeader, out var expected))
            return null;
        return Guid.TryParse(expected.ToString(), out var id) && id == userId
            ? null
            : Results.Problem(title: "A sessão é de outro usuário.", statusCode: StatusCodes.Status409Conflict);
    }
}
