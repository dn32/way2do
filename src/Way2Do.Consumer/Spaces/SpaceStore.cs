using System.Collections.Concurrent;

namespace Way2Do.Consumer.Spaces;

/// <summary>
/// Todos os espaços, em memória. Perder tudo num reinício é aceitável: o próximo evento cria o espaço de novo,
/// recebe 205 e o Way2Do reenvia o retrato completo.
/// </summary>
public sealed class SpaceStore(SpaceLimits limits, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Space> _spaces = new(StringComparer.Ordinal);
    private int _count;

    public Space? Find(string spaceId) => _spaces.GetValueOrDefault(spaceId);

    /// <summary>
    /// Devolve o espaço, criando-o se preciso. <c>Created</c> diz se esta chamada o criou (espaço vazio: a
    /// resposta pede o retrato). <c>Space</c> nulo: limite de espaços atingido.
    /// </summary>
    public (Space? Space, bool Created) GetOrCreate(string spaceId)
    {
        if (_spaces.TryGetValue(spaceId, out var existing))
            return (existing, false);

        // Reserva a vaga antes de criar: duas criações simultâneas não passam juntas do limite.
        if (Interlocked.Increment(ref _count) > limits.MaxSpaces)
        {
            Interlocked.Decrement(ref _count);
            return (_spaces.GetValueOrDefault(spaceId), false);
        }

        var created = new Space(spaceId, time.GetUtcNow(), limits);
        if (_spaces.TryAdd(spaceId, created))
            return (created, true);

        // Outra requisição criou o mesmo espaço primeiro: devolve a vaga e usa o dela.
        Interlocked.Decrement(ref _count);
        return (_spaces[spaceId], false);
    }
}
