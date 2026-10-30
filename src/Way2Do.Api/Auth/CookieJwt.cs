using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Way2Do.Api.Auth;

public static class CookieJwt
{
    /// <summary>
    /// Autenticação por JWT lido só do cookie da sessão (HttpOnly: o JavaScript da página não o alcança). Emissor,
    /// audiência, chave e algoritmo vêm do mesmo <see cref="AuthSettings"/> usado para emitir o token.
    /// </summary>
    public static IServiceCollection AddCookieJwtAuthentication(this IServiceCollection services, AuthSettings auth)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = AuthSettings.Issuer,
                    ValidAudience = AuthSettings.Audience,
                    IssuerSigningKey = auth.SecurityKey,
                    ValidAlgorithms = [AuthSettings.Algorithm],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                // Lê o token só do cookie da sessão. Sem cookie, encerra aqui: senão o handler procuraria o token no
                // cabeçalho Authorization, que não é aceito.
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Cookies[auth.CookieName];
                        if (string.IsNullOrEmpty(token))
                            ctx.NoResult();
                        else
                            ctx.Token = token;
                        return Task.CompletedTask;
                    },
                };
            });
        return services;
    }
}
