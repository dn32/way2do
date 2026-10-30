using Way2Do.Api.Data;
using Way2Do.Api.Domain;

namespace Way2Do.Api.Tests.Domain;

public sealed class TodoFormatTests
{
    [Fact]
    public void Texto_dos_enums_vai_e_volta_para_todos_os_valores()
    {
        foreach (var p in Enum.GetValues<TodoPriority>())
            Assert.Equal(p, TodoFormat.ParsePriority(TodoFormat.Priority(p)));
        foreach (var s in Enum.GetValues<TodoStatus>())
            Assert.Equal(s, TodoFormat.ParseStatus(TodoFormat.Status(s)));
        Assert.Null(TodoFormat.ParsePriority("High")); // o contrato é em minúsculas
        Assert.Null(TodoFormat.ParseStatus("concluida"));
    }

    [Theory]
    [InlineData("high", TodoPriority.High)]
    [InlineData("High", TodoPriority.High)]
    public void Leitura_do_banco_tolera_maiusculas(string text, TodoPriority expected)
    {
        Assert.Equal(expected, TodoFormat.PriorityFromDb(text));
    }
}
