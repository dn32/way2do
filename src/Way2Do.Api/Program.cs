using Way2Do.Api.Auth;
using Way2Do.Api.Data;
using Way2Do.Api.Hosting;
using Way2Do.Api.Sync;
using Way2Do.Api.Webhooks;

// Roteiro da API: serviços, pipeline, rotas e subida. Os detalhes de cada grupo ficam no *Setup da pasta dele.

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// 1. Serviços
// Corpo de requisição de até 1 MB (o maior é o push: até 500 operações), o mesmo limite que o push declara.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = SyncEndpoints.MaxPushBodyBytes);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddTrustedProxies(config);
builder.Services.AddDatabase(config);
builder.Services.AddAuth(config, builder.Environment);
builder.Services.AddSync();
builder.Services.AddWebhooks(config);

var app = builder.Build();
app.LogLocalOnlySettings();

// 2. Pipeline (a ordem importa)
app.UseForwardedHeaders(); // primeiro: o limite de tentativas precisa do IP real
app.UseExceptionHandler();
app.UseApiResponseHeaders();
app.UseMiddleware<OriginCheckMiddleware>();
app.UseAuthentication();
app.UseRateLimiter(); // depois da autenticação: o limite do cadastro de integrações é por usuário
app.UseAuthorization();

// 3. Rotas
app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapSyncEndpoints();
app.MapIntegrationEndpoints();

// 4. Subida
await app.InitializeDatabaseAsync();
app.Run();

public partial class Program;
