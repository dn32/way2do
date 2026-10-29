using System.Globalization;
using System.Text;

namespace Way2Do.Api.Domain;

/// <summary>
/// Normalização de texto compartilhada com o frontend. Cada método documenta o equivalente exato em JavaScript, para
/// que regra "urgente", busca e identidade de tags deem o mesmo resultado nos dois lados.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Minúsculas e sem acentos, usado pela regra "urgente" e pela busca. Em JavaScript:
    /// <c>texto.normalize('NFD').replace(/\p{Mn}/gu, '').toLowerCase().replace(/ς/g, 'σ')</c>
    /// (o .NET não tem a forma final do sigma; a troca deixa os dois lados iguais).
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var sb = new StringBuilder(text.Length);
        // Por code point (Rune), não por char: marcas fora do plano básico também saem, como no \p{Mn} do JS.
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
                sb.Append(rune.ToString());
        }
        return LowerShared(sb.ToString());
    }

    /// <summary>
    /// Minúsculas iguais nos dois lados. Em JavaScript:
    /// <c>texto.replace(/İ/g, 'i').toLowerCase().replace(/ς/g, 'σ')</c>. As duas trocas cobrem as únicas
    /// diferenças entre o <c>toLowerCase()</c> do JS e o <c>ToLowerInvariant()</c> do .NET: o İ turco (o .NET o mantém,
    /// o JS gera "i" + ponto combinante; os dois passam a gerar "i") e o sigma final (só o JS gera "ς").
    /// </summary>
    public static string LowerShared(string text) => text.Replace('İ', 'i').ToLowerInvariant().Replace('ς', 'σ');

    /// <summary>
    /// Texto que o Postgres consegue gravar: sem caractere nulo (U+0000) e sem metade de par substituto (UTF-16
    /// inválido). O mesmo critério vale no frontend.
    /// </summary>
    public static bool IsStorable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
                return false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return false;
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Letra ou dígito decimal, por code point. Em JavaScript: <c>/[\p{L}\p{Nd}]/u</c>.
    /// </summary>
    public static bool IsWordRune(Rune rune) => Rune.IsLetter(rune) || Rune.IsDigit(rune);

    /// <summary>
    /// Remove das pontas os mesmos caracteres que o <c>trim()</c> do JavaScript: espaços Unicode, quebras de linha e
    /// U+FEFF (que o .NET não considera espaço), mas não U+0085 (que só o .NET considera).
    /// </summary>
    public static string TrimLikeJavaScript(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsJavaScriptWhiteSpace(text[start]))
            start++;
        while (end > start && IsJavaScriptWhiteSpace(text[end - 1]))
            end--;
        return text[start..end];
    }

    private static bool IsJavaScriptWhiteSpace(char c) => c == '\uFEFF' || (c != '\u0085' && char.IsWhiteSpace(c));
}
