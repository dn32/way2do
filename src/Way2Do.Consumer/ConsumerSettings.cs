namespace Way2Do.Consumer;

/// <summary>
/// URLs base do consumidor. São duas porque, no ambiente local, a API e o navegador enxergam o consumidor por
/// endereços diferentes (a API pela rede do Compose, o navegador pelo localhost).
/// </summary>
public sealed record ConsumerSettings(string IngestBaseUrl, string ViewBaseUrl)
{
    public static ConsumerSettings FromConfiguration(IConfiguration config) => new(
        Required(config, "Consumer:IngestBaseUrl"),
        Required(config, "Consumer:ViewBaseUrl"));

    /// <summary>URL de entrada: a que se cadastra na tela Integrações do Way2Do (contém o token secreto).</summary>
    public string IngestUrl(string token) => $"{IngestBaseUrl}/in/{token}";

    /// <summary>URL do painel do espaço; é também o <c>dashboardUrl</c> devolvido no ping.</summary>
    public string ViewUrl(string spaceId) => $"{ViewBaseUrl}/v/{spaceId}";

    // Sem as URLs base, o par gerado e o dashboardUrl sairiam quebrados; melhor não subir.
    private static string Required(IConfiguration config, string key)
    {
        var value = config[key]?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException(
                $"{key} não configurada: informe a URL base do consumidor (ex.: http://localhost:5200).");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
            throw new InvalidOperationException($"{key} precisa ser uma URL http ou https absoluta, sem query: '{value}'.");

        // Sem barra final: as rotas são acrescentadas com a própria barra.
        return value.TrimEnd('/');
    }
}
