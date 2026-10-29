using System.Net;
using Way2Do.Api.Webhooks;
using Kind = Way2Do.Api.Webhooks.NetworkGuard.AddressKind;

namespace Way2Do.Api.Tests;

public sealed class NetworkGuardTests
{
    [Theory]
    [InlineData("8.8.8.8", null)]
    [InlineData("104.16.0.1", null)]
    [InlineData("127.0.0.1", Kind.Loopback)]
    [InlineData("127.255.255.254", Kind.Loopback)]
    [InlineData("0.0.0.0", Kind.Reserved)]
    [InlineData("10.20.30.40", Kind.Private)]
    [InlineData("172.16.0.1", Kind.Private)]
    [InlineData("172.31.255.255", Kind.Private)]
    [InlineData("172.32.0.1", null)]
    [InlineData("192.168.0.10", Kind.Private)]
    [InlineData("100.64.0.1", Kind.Private)]
    [InlineData("100.128.0.1", null)]
    [InlineData("169.254.169.254", Kind.LinkLocal)]
    [InlineData("192.0.2.10", Kind.Reserved)]
    [InlineData("198.18.0.1", Kind.Reserved)]
    [InlineData("224.0.0.1", Kind.Reserved)]
    [InlineData("255.255.255.255", Kind.Reserved)]
    [InlineData("::1", Kind.Loopback)]
    [InlineData("::", Kind.Reserved)]
    [InlineData("::ffff:10.0.0.1", Kind.Private)]
    [InlineData("::ffff:169.254.169.254", Kind.LinkLocal)]
    [InlineData("::ffff:8.8.8.8", null)]
    [InlineData("fc00::1", Kind.Private)]
    [InlineData("fd12:3456::1", Kind.Private)]
    [InlineData("fe80::1", Kind.LinkLocal)]
    [InlineData("ff02::1", Kind.Reserved)]
    [InlineData("2001:db8::1", Kind.Reserved)]
    [InlineData("64:ff9b::a00:1", Kind.Private)] // NAT64 de 10.0.0.1
    [InlineData("64:ff9b::808:808", null)] // NAT64 de 8.8.8.8
    [InlineData("2002:a9fe:a9fe::1", Kind.LinkLocal)] // 6to4 de 169.254.169.254
    [InlineData("2606:4700:4700::1111", null)]
    public void Classifica_o_endereco(string address, Kind? expected)
    {
        Assert.Equal(expected, NetworkGuard.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.1")]
    public async Task Sem_a_liberacao_local_rede_interna_e_recusada(string host)
    {
        var guard = new NetworkGuard(allowPrivateNetworks: false);
        await Assert.ThrowsAsync<BlockedDestinationException>(() => guard.ResolveAsync(host, CancellationToken.None));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("169.254.169.254", false)] // metadados de nuvem: nunca, nem no ambiente local
    [InlineData("224.0.0.1", false)]
    public async Task Liberacao_local_abre_so_rede_privada_e_loopback(string host, bool allowed)
    {
        var guard = new NetworkGuard(allowPrivateNetworks: true);
        var resolve = () => guard.ResolveAsync(host, CancellationToken.None);
        if (allowed)
            Assert.Single(await resolve());
        else
            await Assert.ThrowsAsync<BlockedDestinationException>(resolve);
    }

    [Theory]
    [InlineData("https://consumer.example.com/in/abc", null)]
    [InlineData("http://consumer:8080/in/abc", null)]
    [InlineData("", "informe")]
    [InlineData("consumer.example.com/in/abc", "http://")]
    [InlineData("ftp://consumer.example.com/in/abc", "http://")]
    [InlineData("javascript:alert(1)", "http://")]
    [InlineData("https://user:senha@consumer.example.com/in/abc", "usuário e senha")]
    public void Valida_o_formato_da_url(string url, string? error)
    {
        var (uri, message) = WebhookUrl.Parse(url);
        if (error is null)
            Assert.NotNull(uri);
        else
            Assert.Contains(error, message);
    }

    [Fact]
    public void Mascara_mostra_so_o_servidor_e_o_fim_do_token()
    {
        var masked = WebhookUrl.Mask("https://way2do-consumer.example.com/in/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde");
        Assert.Equal("https://way2do-consumer.example.com/…bcde", masked);
    }

    [Theory]
    [InlineData("https://consumer.example.com/v/abc", "https://consumer.example.com/v/abc")]
    [InlineData("javascript:alert(document.cookie)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("/v/relativa", null)]
    public void Link_do_painel_so_aceita_http_e_https(string dashboard, string? expected)
    {
        Assert.Equal(expected, WebhookUrl.SafeDashboardUrl(dashboard));
    }

    [Fact]
    public void Backoff_dobra_ate_5_minutos_com_jitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.5), WebhookDispatcher.Backoff(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(1), WebhookDispatcher.Backoff(1, 1));
        Assert.Equal(TimeSpan.FromSeconds(8), WebhookDispatcher.Backoff(4, 1));
        Assert.Equal(TimeSpan.FromMinutes(5), WebhookDispatcher.Backoff(40, 1));
    }
}
