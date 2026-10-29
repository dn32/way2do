// Normalização de texto compartilhada com o servidor (src/Way2Do.Api/Domain/TextNormalizer.cs). Cada função tem o
// mesmo resultado da equivalente em C#, para que regra "urgente", busca e identidade de tags concordem nos dois lados.

/** Minúsculas e sem acentos (regra "urgente" e busca). */
export function normalize(text: string | null | undefined): string {
  if (!text) return ''
  return lowerShared(text.normalize('NFD').replace(/\p{Mn}/gu, ''))
}

/**
 * Minúsculas iguais às do servidor (TextNormalizer.LowerShared). As duas trocas cobrem as únicas diferenças entre o
 * toLowerCase() do JS e o ToLowerInvariant() do .NET: o İ turco (U+0130; o .NET o mantém e o JS gera "i" + ponto
 * combinante, os dois passam a gerar "i") e o sigma final (só o JS gera "ς"). Casos em tests/fixtures/tags.json.
 */
export function lowerShared(text: string): string {
  return text.replace(/İ/g, 'i').toLowerCase().replace(/ς/g, 'σ')
}

/** Letra ou dígito decimal, por code point (Rune.IsLetter || Rune.IsDigit no .NET). */
export function isWordChar(ch: string): boolean {
  return /^[\p{L}\p{Nd}]$/u.test(ch)
}

/** Espaço pela propriedade Unicode White_Space: a mesma do Rune.IsWhiteSpace do .NET (o \s do JS difere). */
export function isWhiteSpace(ch: string): boolean {
  return /^\p{White_Space}$/u.test(ch)
}

/** Texto que o Postgres consegue gravar: sem caractere nulo e sem metade de par substituto (UTF-16 inválido). */
export function isStorable(text: string): boolean {
  return !/\u0000|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(text)
}
