namespace Way2Do.Api.Domain;

/// <summary>
/// Regra "urgente" (escopo, seção 4): a tarefa é urgente se o título ou a descrição tiver ao menos uma menção não
/// negada a uma palavra que começa com "urgen" (urgente, urgência, urgentemente…), ignorando maiúsculas e acentos.
/// Determinística e idêntica à versão do frontend; os casos ficam em <c>tests/fixtures/urgencia.json</c>.
/// </summary>
public static class UrgencyRule
{
    private const string Stem = "urgen";
    private const int MaxLookbackWords = 3;

    // Já normalizadas (sem acento): "não" → "nao", "ñ" → "n".
    private static readonly HashSet<string> Negations =
        ["nao", "n", "nem", "sem", "nunca", "jamais", "nada", "nenhum", "nenhuma"];

    // Palavras que podem ficar entre a negação e o termo sem desfazer a negação ("não é muito urgente").
    private static readonly HashSet<string> Connectives =
        ["e", "eh", "esta", "ta", "tem", "ha", "foi", "era", "sera", "seja", "for", "mais", "muito", "tao", "tanto",
         "tipo", "de", "da", "do", "um", "uma"];

    public static bool IsUrgent(string? title, string? description) =>
        HasUrgentMention(title) || HasUrgentMention(description);

    public static bool HasUrgentMention(string? text)
    {
        var tokens = Tokenize(TextNormalizer.Normalize(text));
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is { IsWord: true } token && token.Text.StartsWith(Stem, StringComparison.Ordinal) && !IsNegated(tokens, i))
                return true;
        }
        return false;
    }

    // Anda para trás até 3 palavras: negação → negada; palavra de ligação → continua; qualquer outra palavra ou
    // pontuação → para (a negação não atravessa vírgula, ponto nem palavra "de conteúdo").
    private static bool IsNegated(List<Token> tokens, int termIndex)
    {
        var words = 0;
        for (var j = termIndex - 1; j >= 0 && words < MaxLookbackWords; j--)
        {
            var token = tokens[j];
            if (!token.IsWord)
                return false;
            if (Negations.Contains(token.Text))
                return true;
            if (!Connectives.Contains(token.Text))
                return false;
            words++;
        }
        return false;
    }

    // Palavra = sequência de letras/dígitos (por code point). Espaço separa sem gerar token; quebra de linha e qualquer
    // outro símbolo viram pontuação.
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var word = new System.Text.StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (TextNormalizer.IsWordRune(rune))
            {
                word.Append(rune.ToString());
                continue;
            }
            if (word.Length > 0)
            {
                tokens.Add(new Token(word.ToString(), IsWord: true));
                word.Clear();
            }
            if (rune.Value is '\n' or '\r' || !System.Text.Rune.IsWhiteSpace(rune))
                tokens.Add(new Token(rune.ToString(), IsWord: false));
        }
        if (word.Length > 0)
            tokens.Add(new Token(word.ToString(), IsWord: true));
        return tokens;
    }

    private readonly record struct Token(string Text, bool IsWord);
}
