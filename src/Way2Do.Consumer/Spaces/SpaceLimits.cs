namespace Way2Do.Consumer.Spaces;

/// <summary>
/// Limites contra abuso. Qualquer token bem formado cria um espaço, e o estado é todo em memória: sem teto, quem
/// quisesse derrubar o consumidor bastaria inventar tokens ou mandar tarefas sem fim.
/// </summary>
public sealed record SpaceLimits(
    int MaxSpaces = SpaceLimits.DefaultMaxSpaces,
    int MaxTodosPerSpace = SpaceLimits.DefaultMaxTodosPerSpace,
    int MaxSeenEventIds = SpaceLimits.DefaultMaxSeenEventIds)
{
    /// <summary>Espaços simultâneos. Acima disso, novos espaços são recusados com 503 até o consumidor reiniciar.</summary>
    public const int DefaultMaxSpaces = 1000;

    /// <summary>Tarefas por espaço, contando as excluídas (que continuam no painel).</summary>
    public const int DefaultMaxTodosPerSpace = 5000;

    /// <summary>
    /// eventIds lembrados por espaço para a deduplicação; acima disso, esquece os mais antigos. Uma reentrega tão
    /// atrasada ainda é barrada pela regra de versão, então esquecer o id não reaplica estado velho.
    /// </summary>
    public const int DefaultMaxSeenEventIds = 20_000;

    /// <summary>Eventos guardados para o bloco "Últimos eventos" do painel.</summary>
    public const int RecentEvents = 20;

    /// <summary>Corpo máximo de um evento. Uma tarefa no limite do Way2Do (título 200, descrição 4.000, 20 tags de
    /// 40) cabe com folga.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    // Campos de texto guardados com corte: o Way2Do já respeita esses limites; o corte só segura quem escreve
    // direto num espaço próprio, para a memória de cada tarefa ficar previsível.
    public const int MaxTitle = 200;
    public const int MaxDescription = 4000;
    public const int MaxTags = 20;
    public const int MaxTag = 40;
    public const int MaxShortText = 100;
}
