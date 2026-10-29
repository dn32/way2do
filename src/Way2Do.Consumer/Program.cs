using Way2Do.Consumer;
using Way2Do.Consumer.Spaces;

var builder = WebApplication.CreateBuilder(args);

// O token de entrada viaja no caminho (/in/<T>). O log de requisições do ASP.NET Core registra o caminho completo,
// então fica desligado no código (filtro registrado depois do appsettings, vence uma configuração igual).
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

// Nenhuma requisição legítima passa de 64 KB (o maior corpo é um evento).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = SpaceLimits.MaxBodyBytes);

builder.Services.AddSingleton(ConsumerSettings.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SpaceLimits());
builder.Services.AddSingleton<SpaceStore>();

var app = builder.Build();

// Cabeçalhos de segurança em todas as respostas. O conteúdo das tarefas vem de fora (qualquer um escreve no próprio
// espaço): a página só usa textContent, e a CSP sem inline é a segunda barreira contra XSS. no-referrer e no-store
// porque o H (e, na entrada, o token) está no endereço.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var headers = ctx.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles();
app.MapIngestEndpoints();
app.MapDashboardEndpoints();

app.Run();

public partial class Program;
