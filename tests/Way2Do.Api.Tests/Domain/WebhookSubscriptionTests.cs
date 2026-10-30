using Way2Do.Api.Data;

namespace Way2Do.Api.Tests.Domain;

public sealed class WebhookSubscriptionTests
{
    [Fact]
    public void Retrato_pedido_dentro_do_intervalo_fica_para_o_fim_dele_e_pedidos_repetidos_se_juntam()
    {
        var now = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        var subscription = new WebhookSubscription { Url = "https://c.example.com/in/x", LastResyncAt = now.AddSeconds(-20) };

        subscription.RequestResync(now);
        subscription.RequestResync(now.AddSeconds(5));

        Assert.Equal(now.AddSeconds(40), subscription.ResyncDueAt);
    }
}
