using System.Threading.RateLimiting;
using Way2Do.Api.Auth;

namespace Way2Do.Api.Webhooks;

public static class WebhooksSetup
{
    /// <summary>
    /// Integração externa: proteção contra SSRF, cliente HTTP das entregas, worker do outbox (desligável com
    /// Webhooks:WorkerEnabled; os testes chamam o despachante diretamente) e limite de cadastros por usuário.
    /// </summary>
    public static IServiceCollection AddWebhooks(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new NetworkGuard(config.GetValue("Webhooks:AllowPrivateNetworks", false)));
        services.AddSingleton<WebhookHttp>();
        services.AddSingleton<WebhookSignal>();
        services.AddSingleton<WebhookDispatcher>();
        if (config.GetValue("Webhooks:WorkerEnabled", true))
            services.AddHostedService<WebhookWorker>();

        // Cada cadastro faz uma requisição do servidor para fora (o ping): limite por usuário, para a tela não virar
        // um disparador de requisições para terceiros.
        var permits = config.GetValue("Webhooks:RegisterRateLimit:PermitLimit", 10);
        var window = TimeSpan.FromSeconds(config.GetValue("Webhooks:RegisterRateLimit:WindowSeconds", 60));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(IntegrationEndpoints.RegisterRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.GetUser()?.Id.ToString() ?? http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 }));
        });
        return services;
    }
}
