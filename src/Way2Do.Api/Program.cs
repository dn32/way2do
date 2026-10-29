using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Way2Do.Api.Auth;
using Way2Do.Api.Data;
using Way2Do.Api.Sync;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Corpo de requisição de até 1 MB (o maior é o push: até 500 operações).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);

// Banco. A senha pode vir separada (POSTGRES_PASSWORD do .env), para a connection string não carregar segredo.
var connection = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada."));
if (string.IsNullOrEmpty(connection.Password) && config["POSTGRES_PASSWORD"] is { Length: > 0 } dbPassword)
    connection.Password = dbPassword;
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection.ConnectionString));

// Autenticação: JWT num cookie HttpOnly (o token não fica acessível ao JavaScript da página).
var auth = AuthSettings.FromConfiguration(config, builder.Environment);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<LoginTimingGuard>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = AuthSettings.Issuer,
            ValidAudience = AuthSettings.Audience,
            IssuerSigningKey = auth.SecurityKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
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
builder.Services.AddAuthorization();

// IP real do cliente, aceito só de proxies confiáveis (Network:TrustedProxies); sem isso o limite de tentativas veria
// todo mundo com o IP do proxy. A API confia num único salto, o nginx: é ele que resolve o IP real e o entrega em
// X-Forwarded-For SUBSTITUINDO o cabeçalho (na VPS, a partir do CF-Connecting-IP, aceito só do cloudflared). Por
// isso ForwardLimit = 1 basta e um X-Forwarded-For forjado pelo cliente nunca chega aqui.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    foreach (var cidr in config.GetSection("Network:TrustedProxies").Get<string[]>() ?? [])
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

// Limite de tentativas de login por IP, contra força bruta.
var loginPermits = config.GetValue("Auth:LoginRateLimit:PermitLimit", 10);
var loginWindow = TimeSpan.FromSeconds(config.GetValue("Auth:LoginRateLimit:WindowSeconds", 60));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPermits, Window = loginWindow, QueueLimit = 0 }));
});

builder.Services.AddScoped<SyncService>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

if (auth.SigningKeyGenerated)
    app.Logger.LogWarning("Jwt:SigningKey não configurada: chave aleatória gerada; as sessões caem a cada reinício.");

app.UseForwardedHeaders();
app.UseExceptionHandler();

// Respostas da API nunca vão para cache (dados do usuário).
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

app.UseMiddleware<OriginCheckMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapSyncEndpoints();

if (config.GetValue("Database:InitializeOnStartup", true))
    await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

public partial class Program;
