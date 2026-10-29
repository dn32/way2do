using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Way2Do.Api.Data;

namespace Way2Do.Api.Auth;

public sealed record LoginRequest(string? UserName, string? Password);

public sealed record UserResponse(Guid Id, string Name);

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";
    private const string InvalidCredentialsMessage = "Usuário ou senha inválidos.";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapPost("/login", Login).RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/logout", Logout);
        group.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Login(LoginRequest request, AppDbContext db, IPasswordHasher<User> hasher,
        LoginTimingGuard timing, TokenService tokens, AuthSettings settings, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
            return InvalidCredentials();

        var userName = request.UserName.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == userName, ct);
        if (user is null)
        {
            timing.SpendVerificationCost(request.Password);
            return InvalidCredentials();
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            return InvalidCredentials();
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync(ct);
        }

        var token = tokens.Issue(user);
        http.Response.Cookies.Append(settings.CookieName, token.Value, SessionCookie(settings, token.ExpiresAt));
        return Results.Ok(new UserResponse(user.Id, user.UserName));
    }

    // Aceita sessão expirada ou ausente: o objetivo é só garantir que o cookie seja apagado.
    private static IResult Logout(AuthSettings settings, HttpContext http)
    {
        http.Response.Cookies.Delete(settings.CookieName, SessionCookie(settings, expires: null));
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? Results.Ok(new UserResponse(id, principal.FindFirstValue(JwtRegisteredClaimNames.Name) ?? ""))
            : Results.Unauthorized();

    // HttpOnly: o JavaScript da página não lê o token. SameSite=Strict: o cookie não sai em requisições de outros
    // sites (CSRF). Secure: só trafega em HTTPS (desligável apenas no ambiente local em http://localhost).
    private static CookieOptions SessionCookie(AuthSettings settings, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = settings.SecureCookie,
        SameSite = SameSiteMode.Strict,
        Path = AuthSettings.CookiePath,
        Expires = expires,
        IsEssential = true,
    };

    private static IResult InvalidCredentials() =>
        Results.Problem(title: InvalidCredentialsMessage, statusCode: StatusCodes.Status401Unauthorized);
}
