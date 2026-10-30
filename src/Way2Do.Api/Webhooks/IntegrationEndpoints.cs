using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Way2Do.Api.Auth;
using Way2Do.Api.Data;

namespace Way2Do.Api.Webhooks;

public sealed record IntegrationRequest(string? Url);

/// <summary>Assinatura como a tela vê: a URL de entrada sempre mascarada (ela contém o token do consumidor).</summary>
public sealed record IntegrationResponse(Guid Id, string Url, string? DashboardUrl, DateTimeOffset CreatedAt)
{
    public static IntegrationResponse From(WebhookSubscription s) => new(s.Id, WebhookUrl.Mask(s.Url), s.DashboardUrl, s.CreatedAt);
}

/// <summary>
/// Tela Integrações (escopo, seção 6): cada usuário cadastra as URLs que recebem as tarefas dele. O cadastro valida o
/// formato, recusa redes internas (SSRF) e só grava se o consumidor responder 2xx ao <c>webhook.ping</c>.
/// </summary>
public static class IntegrationEndpoints
{
    public const string RegisterRateLimitPolicy = "integrations-register";
    public const int MaxSubscriptionsPerUser = 10;

    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations").RequireAuthorization();
        group.MapGet("", List);
        group.MapPost("", Register).RequireRateLimiting(RegisterRateLimitPolicy);
        group.MapDelete("/{id:guid}", Remove);
    }

    private static async Task<IResult> List(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (principal.GetUser() is not { } user)
            return Results.Unauthorized();
        var subscriptions = await db.WebhookSubscriptions
            .Where(s => s.UserId == user.Id)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(subscriptions.Select(IntegrationResponse.From));
    }

    private static async Task<IResult> Register(IntegrationRequest? request, ClaimsPrincipal principal, AppDbContext db,
        NetworkGuard guard, WebhookHttp http, WebhookSignal signal, TimeProvider clock, ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (principal.GetUser() is not { } user)
            return Results.Unauthorized();

        var logger = loggers.CreateLogger("Way2Do.Api.Webhooks.Integrations");

        // 1) Formato.
        var (uri, error) = WebhookUrl.Parse(request?.Url);
        if (uri is null)
            return Refused(error!);
        var url = uri.AbsoluteUri;

        if (await db.WebhookSubscriptions.AnyAsync(s => s.UserId == user.Id && s.Url == url, ct))
            return Refused("esta URL já está cadastrada", StatusCodes.Status409Conflict);
        if (await db.WebhookSubscriptions.CountAsync(s => s.UserId == user.Id, ct) >= MaxSubscriptionsPerUser)
            return Refused($"limite de {MaxSubscriptionsPerUser} integrações por usuário atingido");

        // 2) Proteção contra SSRF (refeita a cada conexão pelo WebhookHttp; aqui dá a mensagem certa ao usuário).
        try
        {
            await guard.ResolveAsync(uri.IdnHost, ct);
        }
        catch (BlockedDestinationException blocked)
        {
            return Refused(blocked.Message);
        }

        // 3) Teste de entrega: só grava com 2xx em até 5 s.
        var now = clock.GetUtcNow();
        var eventId = Guid.NewGuid();
        var ping = await http.PostAsync(url, eventId, OutboxWriter.Ping, OutboxWriter.PingPayload(eventId, now),
            WebhookHttp.PingTimeout, ct);
        if (!ping.Delivered)
        {
            logger.LogInformation("Cadastro de {Url} recusado: {Error}", WebhookUrl.Mask(url), ping.Error);
            return Refused($"o teste de entrega falhou: {ping.Error}");
        }

        // 4) Link do painel, se o consumidor informou um (só http/https).
        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Url = url,
            DashboardUrl = ping.ReadDashboardUrl(),
            CreatedAt = now,
        };
        // 5) Retrato completo para toda integração recém-cadastrada, com 200 ou 205: o 200 só diz que o espaço já
        // existe, não que ele tem as tarefas DESTE usuário (outro usuário na mesma URL, ou recadastro depois de
        // editar). O que o consumidor já tiver na mesma versão, ele descarta.
        subscription.RequestResync(now);

        db.WebhookSubscriptions.Add(subscription);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Refused("esta URL já está cadastrada", StatusCodes.Status409Conflict); // cadastro simultâneo
        }
        signal.Notify();

        logger.LogInformation("Integração {Url} cadastrada.", WebhookUrl.Mask(url));
        return Results.Created($"/api/integrations/{subscription.Id}", IntegrationResponse.From(subscription));
    }

    /// <summary>Remove a assinatura; as entregas pendentes dela vão junto (cascata no banco).</summary>
    private static async Task<IResult> Remove(Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (principal.GetUser() is not { } user)
            return Results.Unauthorized();
        var removed = await db.WebhookSubscriptions.Where(s => s.Id == id && s.UserId == user.Id).ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static IResult Refused(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(title: "Não foi possível cadastrar a integração.", detail: Capitalize(message), statusCode: status);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";
}
