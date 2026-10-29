import { isWhiteSpace, isWordChar, normalize } from './text'

// Regra "urgente" (escopo, seção 4), idêntica a src/Way2Do.Api/Domain/UrgencyRule.cs. Casos compartilhados em
// tests/fixtures/urgencia.json.

const STEM = 'urgen'
const MAX_LOOKBACK_WORDS = 3

const NEGATIONS = new Set(['nao', 'n', 'nem', 'sem', 'nunca', 'jamais', 'nada', 'nenhum', 'nenhuma'])

const CONNECTIVES = new Set([
  'e', 'eh', 'esta', 'ta', 'tem', 'ha', 'foi', 'era', 'sera', 'seja', 'for', 'mais', 'muito', 'tao', 'tanto',
  'tipo', 'de', 'da', 'do', 'um', 'uma',
])

interface Token {
  text: string
  isWord: boolean
}

export function isUrgent(title: string | null | undefined, description: string | null | undefined): boolean {
  return hasUrgentMention(title) || hasUrgentMention(description)
}

export function hasUrgentMention(text: string | null | undefined): boolean {
  const tokens = tokenize(normalize(text))
  return tokens.some((t, i) => t.isWord && t.text.startsWith(STEM) && !isNegated(tokens, i))
}

// Anda para trás até 3 palavras: negação → negada; palavra de ligação → continua; qualquer outra palavra ou
// pontuação → para.
function isNegated(tokens: Token[], termIndex: number): boolean {
  let words = 0
  for (let j = termIndex - 1; j >= 0 && words < MAX_LOOKBACK_WORDS; j--) {
    const token = tokens[j]
    if (!token.isWord) return false
    if (NEGATIONS.has(token.text)) return true
    if (!CONNECTIVES.has(token.text)) return false
    words++
  }
  return false
}

// Palavra = sequência de letras/dígitos (por code point). Espaço separa sem gerar token; quebra de linha e qualquer
// outro símbolo viram pontuação.
function tokenize(text: string): Token[] {
  const tokens: Token[] = []
  let word = ''
  for (const ch of text) {
    if (isWordChar(ch)) {
      word += ch
      continue
    }
    if (word) {
      tokens.push({ text: word, isWord: true })
      word = ''
    }
    if (ch === '\n' || ch === '\r' || !isWhiteSpace(ch)) tokens.push({ text: ch, isWord: false })
  }
  if (word) tokens.push({ text: word, isWord: true })
  return tokens
}
