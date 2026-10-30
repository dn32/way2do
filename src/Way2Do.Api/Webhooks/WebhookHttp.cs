using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Way2Do.Api.Webhooks;

/// <summary>Formato e exibição das URLs de entrada (que contêm o token do consumidor: são segredo).</summary>
public static class WebhookUrl
{
    public const int MaxLength = 2048;

    /// <summary>URL absoluta http/https com host, sem usuário e senha embutidos; devolve o motivo da recusa.</summary>
    public static (Uri? Uri, string? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, "informe a URL de entrada do consumidor");
        text = text.Trim();
        if (text.Length > MaxLength)
            return (null, $"a URL tem mais de {MaxLength} caracteres");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, "use uma URL completa começando com http:// ou https://");
        if (string.IsNullOrEmpty(uri.Host))
            return (null, "a URL não tem servidor");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (null, "a URL não pode conter usuário e senha");
        return (uri, null);
    }

    /// <summary>Link do painel informado pelo consumidor: só http/https (um "javascript:" viraria XSS no clique).</summary>
    public static string? SafeDashboardUrl(string? text) =>
        Parse(text) is ({ } uri, null) ? uri.AbsoluteUri : null;

    /// <summary>Versão para mostrar e registrar em log: servidor e só o fim do caminho (ex.: https://host/…a1b2).</summary>
    public static string Mask(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "…";
        var path = uri.AbsolutePath.TrimEnd('/');
        var tail = path.Length > 4 ? path[^4..] : "";
        return $"{uri.Scheme}://{uri.Authority}/…{tail}";
    }
}

/// <summary>Resultado de um POST para o consumidor.</summary>
public sealed record WebhookResponse(int? StatusCode, string? Error, string? Body, string? DashboardUrlHeader = null)
{
    /// <summary>
    /// Cabeçalho com o link do painel na resposta ao ping. Existe porque a resposta 205 não pode ter corpo (RFC 9110,
    /// 15.3.6), e o ping de um consumidor novo é justamente 205.
    /// </summary>
    public const string DashboardHeader = "X-Way2Do-Dashboard-Url";

    public bool Delivered => StatusCode is >= 200 and < 300;

    /// <summary>205 Reset Content: entregue, e o consumidor pede o retrato completo.</summary>
    public bool WantsSnapshot => StatusCode == (int)HttpStatusCode.ResetContent;

    /// <summary>
    /// Link do painel informado na resposta ao ping, só se for http/https: o cabeçalho primeiro (único meio no 205,
    /// que não tem corpo); no 200, também o corpo <c>{ "dashboardUrl": "..." }</c>.
    /// </summary>
    /// <remarks>Método, e não propriedade: o <c>ToString</c> do record (logs) não deve ler o corpo.</remarks>
    public string? ReadDashboardUrl() => WebhookUrl.SafeDashboardUrl(DashboardUrlHeader) ?? DashboardUrlFromBody(Body);

    private static string? DashboardUrlFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("dashboardUrl", out var value)
                && value.ValueKind == JsonValueKind.String
                ? WebhookUrl.SafeDashboardUrl(value.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null; // resposta sem JSON: consumidor sem painel
        }
    }
}

/// <summary>
/// Cliente HTTP das integrações: confere o destino a cada conexão (<see cref="NetworkGuard"/>), não segue
/// redirecionamentos, não usa proxy do sistema e lê no máximo 16 KB da resposta.
/// </summary>
public sealed class WebhookHttp : IDisposable
{
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);
    public const int MaxResponseBytes = 16 * 1024;

    private readonly HttpClient _client;

    public WebhookHttp(NetworkGuard guard)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // 3xx = falha: um redirecionamento poderia levar a 169.254.169.254
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                // A regra é refeita aqui, na hora da conexão, com o DNS de agora.
                var addresses = await guard.ResolveAsync(context.DnsEndPoint.Host, ct);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Way2Do-Webhooks", "1.0"));
    }

    public async Task<WebhookResponse> PostAsync(string url, Guid eventId, string eventType, string payload, TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Way2Do-Event-Id", eventId.ToString());
        request.Headers.Add("X-Way2Do-Event-Type", eventType);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            var body = await ReadLimitedAsync(response.Content, cts.Token);
            var dashboard = response.Headers.TryGetValues(WebhookResponse.DashboardHeader, out var values) ? values.FirstOrDefault() : null;
            return status is >= 300 and < 400
                ? new WebhookResponse(status, "o consumidor respondeu com redirecionamento, que não é seguido", body)
                : new WebhookResponse(status, status is >= 200 and < 300 ? null : $"o consumidor respondeu {status}", body, dashboard);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookResponse(null, $"o consumidor não respondeu em {timeout.TotalSeconds:0} s", null);
        }
        catch (HttpRequestException e) when (e.InnerException is BlockedDestinationException blocked)
        {
            return new WebhookResponse(null, blocked.Message, null);
        }
        catch (HttpRequestException)
        {
            return new WebhookResponse(null, "não foi possível conectar ao consumidor", null);
        }
        catch (IOException)
        {
            // A conexão caiu no meio da resposta (inclui HttpIOException): não entregue, tenta de novo.
            return new WebhookResponse(null, "a conexão com o consumidor caiu durante a resposta", null);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    public void Dispose() => _client.Dispose();
}
