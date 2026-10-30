using System.Net;
using System.Text.RegularExpressions;
using Way2Do.Consumer.Spaces;

namespace Way2Do.Consumer.Tests;

public sealed partial class SecurityTests(SharedConsumer consumer) : IClassFixture<SharedConsumer>
{
    private readonly HttpClient _client = consumer.Client();

    public static TheoryData<string> Paths()
    {
        var spaceId = SpaceKeys.SpaceIdOf(Events.NewToken());
        return new() { "/", $"/v/{spaceId}", $"/v/{spaceId}/state", "/dashboard.js", "/way2do.css", "/nao-existe" };
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Cabecalhos_de_seguranca_em_todas_as_respostas(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal("default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
            Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("no-store, no-transform", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task Cabecalhos_de_seguranca_tambem_na_entrada_de_eventos()
    {
        var response = await _client.Send(Events.NewToken(), Events.Ping());

        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    // A CSP sem 'unsafe-inline' já barra, mas o teste pega a regressão antes de alguém precisar afrouxar a CSP.
    [Theory]
    [InlineData("index.html")]
    [InlineData("dashboard.html")]
    public void Paginas_sem_script_estilo_ou_evento_inline(string page)
    {
        var html = File.ReadAllText(WebRoot(page));

        Assert.DoesNotMatch(InlineScript(), html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(StyleAttribute(), html);
        Assert.DoesNotMatch(EventAttribute(), html);
    }

    [Theory]
    [InlineData("dashboard.js")]
    [InlineData("home.js")]
    public void Scripts_nao_usam_innerHTML_nem_insercao_de_HTML(string script)
    {
        var js = File.ReadAllText(WebRoot(script));

        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("outerHTML", js);
        Assert.DoesNotContain("insertAdjacentHTML", js);
        Assert.DoesNotContain("document.write", js);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : "";

    private static string WebRoot(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Way2Do.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Way2Do.Consumer", "wwwroot", file);
    }

    // <script> sem src (com conteúdo) é script inline.
    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();
}
