using System.Text.Json;

namespace Way2Do.Consumer.Spaces;

/// <summary>
/// Páginas e o estado do painel. O painel é somente leitura quanto aos dados; a única ação é o modo de falha, que
/// muda só o comportamento deste consumidor (recurso de demonstração).
/// </summary>
public static class DashboardEndpoints
{
    private const string HtmlType = "text/html; charset=utf-8";

    public sealed record FailureModeRequest(string? Mode);

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var brandCss = LoadBrandCss();

        app.MapGet("/", (IWebHostEnvironment env) => Page(env, "index.html"));

        // CSS da marca (docs/design/way2do.css), embutido no assembly.
        app.MapGet("/way2do.css", () => Results.Bytes(brandCss, "text/css; charset=utf-8"));

        // Gera um par novo. Não grava nada: o espaço só passa a existir quando o primeiro evento chega.
        app.MapPost("/new", (ConsumerSettings settings) =>
        {
            var token = SpaceKeys.NewToken();
            return Results.Json(new
            {
                ingestUrl = settings.IngestUrl(token),
                viewUrl = settings.ViewUrl(SpaceKeys.SpaceIdOf(token)),
            });
        });

        // A página é a mesma para qualquer espaço: o JS lê o H do endereço e busca o estado.
        app.MapGet("/v/{spaceId}", (string spaceId, IWebHostEnvironment env) =>
            SpaceKeys.IsValidSpaceId(spaceId) ? Page(env, "dashboard.html") : Results.NotFound());

        app.MapGet("/v/{spaceId}/state", (string spaceId, SpaceStore store) =>
            SpaceKeys.IsValidSpaceId(spaceId) && store.Find(spaceId) is { } space
                ? Results.Json(space.ToState())
                : Results.NotFound());

        app.MapPost("/v/{spaceId}/failure-mode", SetFailureModeAsync);
    }

    private static async Task<IResult> SetFailureModeAsync(string spaceId, HttpRequest request, SpaceStore store,
        CancellationToken ct)
    {
        if (!SpaceKeys.IsValidSpaceId(spaceId) || store.Find(spaceId) is not { } space)
            return Results.NotFound();

        // Só JSON: um formulário de outro site não consegue enviar application/json sem passar pelo preflight do
        // CORS (que não é liberado aqui), então não liga o modo de falha às escondidas.
        if (!request.HasJsonContentType())
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        FailureModeRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<FailureModeRequest>(ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "JSON inválido" });
        }
        if (Space.ParseMode(body?.Mode) is not { } mode)
            return Results.BadRequest(new { error = "mode deve ser off, down ou flaky" });

        space.SetFailureMode(mode);
        return Results.Json(space.ToState().FailureMode);
    }

    private static IResult Page(IWebHostEnvironment env, string name)
    {
        var file = env.WebRootFileProvider.GetFileInfo(name);
        return file.Exists ? Results.Stream(file.CreateReadStream(), HtmlType) : Results.NotFound();
    }

    private static byte[] LoadBrandCss()
    {
        using var stream = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream("Way2Do.Consumer.way2do.css")
            ?? throw new InvalidOperationException("CSS da marca (way2do.css) não embutido no assembly.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
