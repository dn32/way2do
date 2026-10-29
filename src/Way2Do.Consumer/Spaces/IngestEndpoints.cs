using System.Text.Json;

namespace Way2Do.Consumer.Spaces;

/// <summary>
/// POST /in/&lt;T&gt;: recebe os eventos do Way2Do. Contrato (docs/escopo.md, seção 6): 2xx = entregue; 205 = entregue
/// e "mande o retrato completo"; 5xx = não entregue, o Way2Do tenta de novo com backoff.
/// </summary>
public static class IngestEndpoints
{
    /// <summary>
    /// Cópia do <c>dashboardUrl</c> em cabeçalho, presente em toda resposta ao ping. Necessária porque a resposta
    /// 205 não pode ter corpo (RFC 9110, 15.3.6; o Kestrel recusa a escrita), e o ping de um espaço novo é
    /// justamente 205.
    /// </summary>
    public const string DashboardUrlHeader = "X-Way2Do-Dashboard-Url";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapIngestEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/in/{token}", ReceiveAsync);

    private static async Task<IResult> ReceiveAsync(string token, HttpContext http, SpaceStore store,
        ConsumerSettings settings, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        // Token mal formado é tratado como rota inexistente: não diferencia "token errado" de "endereço errado".
        if (!SpaceKeys.IsValidToken(token))
            return Results.NotFound();
        var spaceId = SpaceKeys.SpaceIdOf(token);

        // Modo "fora do ar" antes de tudo: simula o consumidor que não responde, sem olhar o corpo.
        if (store.Find(spaceId) is { } current && current.RefuseBeforeProcessing())
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var body = await ReadBodyAsync(http.Request, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        WebhookEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<WebhookEvent>(SkipBom(body), Json);
        }
        catch (JsonException)
        {
            return Invalid("JSON inválido ou fora do formato do evento");
        }
        if ((evt is null ? "corpo vazio" : evt.Validate()) is { } error)
            return Invalid(error);

        var (space, created) = store.GetOrCreate(spaceId);
        var log = loggers.CreateLogger("Way2Do.Consumer.Ingest");
        if (space is null)
        {
            // 503, e não 429: é falta de capacidade deste consumidor, não excesso de requisições de quem envia.
            // Para o Way2Do, os dois significam "tente de novo mais tarde".
            log.LogWarning("Limite de espaços atingido; espaço {Space} recusado.", SpaceKeys.Short(spaceId));
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (created)
            log.LogInformation("Espaço {Space} criado.", SpaceKeys.Short(spaceId));

        // Espaço vazio (novo, ou perdido num reinício): pede o retrato completo. Vale também quando o primeiro evento
        // é do retrato (reinício no meio dele: o que veio antes se perdeu). O Way2Do faz no máximo um retrato por
        // minuto por integração, então isso não vira laço.
        var snapshotRequested = created;
        space.Apply(evt!, time.GetUtcNow(), snapshotRequested);
        var status = snapshotRequested ? StatusCodes.Status205ResetContent : StatusCodes.Status200OK;

        // Modo "instável": processou, mas responde 503 (confirmação perdida). Um espaço recém-criado ainda está com o
        // modo desligado; a condição só deixa explícito que o pedido de retrato (205) nunca se perde.
        if (!snapshotRequested && space.DropAcknowledgement())
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (evt!.Type == EventTypes.Ping)
        {
            var dashboardUrl = settings.ViewUrl(spaceId);
            http.Response.Headers[DashboardUrlHeader] = dashboardUrl;
            if (status == StatusCodes.Status200OK)
                return Results.Json(new { dashboardUrl });
        }
        return Results.StatusCode(status);
    }

    // Lê no máximo 64 KB: o Content-Length declarado barra cedo; o corpo em chunks é contado na leitura (e o limite
    // do Kestrel, igual, interrompe quem passar dele).
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > SpaceLimits.MaxBodyBytes)
            return null;

        var buffer = new byte[SpaceLimits.MaxBodyBytes + 1];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(total), ct);
                if (read == 0)
                    return buffer[..total];
                total += read;
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        return null;
    }

    private static ReadOnlySpan<byte> SkipBom(byte[] body) =>
        body.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? body.AsSpan(3) : body;

    private static IResult Invalid(string error) => Results.BadRequest(new { error });
}
