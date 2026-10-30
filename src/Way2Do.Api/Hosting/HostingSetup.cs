using Microsoft.AspNetCore.HttpOverrides;
using Way2Do.Api.Auth;
using Way2Do.Api.Webhooks;

namespace Way2Do.Api.Hosting;

/// <summary>Configuração do servidor HTTP que não pertence a uma funcionalidade.</summary>
public static class HostingSetup
{
    /// <summary>
    /// IP real do cliente, aceito só de proxies confiáveis (Network:TrustedProxies); sem isso o limite de tentativas
    /// veria todo mundo com o IP do proxy. A API confia num único salto, o nginx: é ele que resolve o IP real e o
    /// entrega em X-Forwarded-For SUBSTITUINDO o cabeçalho (na VPS, a partir do CF-Connecting-IP, aceito só do
    /// cloudflared). Por isso ForwardLimit = 1 basta e um X-Forwarded-For forjado pelo cliente nunca chega aqui.
    /// </summary>
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration config) =>
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            foreach (var cidr in config.GetSection("Network:TrustedProxies").Get<string[]>() ?? [])
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });

    /// <summary>Respostas da API nunca vão para cache (dados do usuário).</summary>
    public static IApplicationBuilder UseApiResponseHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.OnStarting(() =>
                {
                    ctx.Response.Headers.CacheControl = "no-store";
                    ctx.Response.Headers.XContentTypeOptions = "nosniff";
                    return Task.CompletedTask;
                });
            }
            await next();
        });

    /// <summary>Avisa no log as configurações que só servem ao ambiente local.</summary>
    public static void LogLocalOnlySettings(this WebApplication app)
    {
        if (app.Services.GetRequiredService<AuthSettings>().SigningKeyGenerated)
            app.Logger.LogWarning("Jwt:SigningKey não configurada: chave aleatória gerada; as sessões caem a cada reinício.");
        if (app.Services.GetRequiredService<NetworkGuard>().AllowPrivateNetworks)
            app.Logger.LogWarning("Webhooks:AllowPrivateNetworks ligado: integrações podem apontar para a rede interna (só no ambiente local).");
    }
}
