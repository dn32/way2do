using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Way2Do.Api.Data;

namespace Way2Do.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Login e sessão: JWT num cookie HttpOnly (o token não fica acessível ao JavaScript da página) e limite de
    /// tentativas de login por IP, contra força bruta (o IP real vem dos proxies confiáveis, em Hosting).
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var auth = AuthSettings.FromConfiguration(config, env);
        services.AddSingleton(auth);
        services.AddSingleton<TokenService>();
        services.AddSingleton<LoginTimingGuard>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddCookieJwtAuthentication(auth);
        services.AddAuthorization();

        var permits = config.GetValue("Auth:LoginRateLimit:PermitLimit", 10);
        var window = TimeSpan.FromSeconds(config.GetValue("Auth:LoginRateLimit:WindowSeconds", 60));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 }));
        });
        return services;
    }
}
