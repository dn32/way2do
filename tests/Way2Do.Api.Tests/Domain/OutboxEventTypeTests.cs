using Way2Do.Api.Data;
using Way2Do.Api.Domain;
using Way2Do.Api.Webhooks;

namespace Way2Do.Api.Tests.Domain;

public sealed class OutboxEventTypeTests
{
    [Theory]
    [InlineData(true, true, false, false, OutboxWriter.Created)]
    [InlineData(true, false, false, false, OutboxWriter.Updated)]
    [InlineData(true, false, false, true, OutboxWriter.Deleted)]
    [InlineData(true, false, true, true, OutboxWriter.Updated)] // já estava excluída: só metadados da exclusão
    [InlineData(true, true, false, true, null)] // criada e excluída no mesmo lote: o consumidor nunca a viu
    [InlineData(false, false, false, false, null)] // nada visível mudou
    [InlineData(false, true, false, false, null)]
    public void Tipo_do_evento_por_mudanca(bool contentChanged, bool isNew, bool deletedBefore, bool deletedNow, string? expected)
    {
        Assert.Equal(expected, OutboxWriter.EventTypeFor(contentChanged, isNew, deletedBefore, deletedNow));
    }
}
