import { OfflineError, SessionExpiredError } from './errors'

/** Integração como o servidor devolve: a URL de entrada vem mascarada (ela contém o token do consumidor). */
export interface Integration {
  id: string
  url: string
  dashboardUrl: string | null
  createdAt: string
}

/** Recusa do servidor com o motivo para o usuário (formato, rede interna, teste de entrega, limite). */
export class IntegrationError extends Error {}

/** A integração não existe mais (removida em outra aba ou aparelho). */
export class IntegrationNotFoundError extends IntegrationError {}

// O cadastro espera o teste de entrega (até 5 s no servidor) e a resolução do endereço.
const TIMEOUT_MS = 15000

export async function listIntegrations(): Promise<Integration[]> {
  const body = await request('/api/integrations', { method: 'GET' })
  if (!Array.isArray(body)) throw new IntegrationError('Resposta inesperada do servidor.')
  return body.map(parseIntegration)
}

export async function registerIntegration(url: string): Promise<Integration> {
  return parseIntegration(await request('/api/integrations', { method: 'POST', body: JSON.stringify({ url }) }))
}

export async function removeIntegration(id: string): Promise<void> {
  try {
    await request(`/api/integrations/${encodeURIComponent(id)}`, { method: 'DELETE' })
  } catch (e) {
    // Só aqui o 404 tem esse sentido; em outra rota seria um erro comum.
    if (e instanceof HttpStatusError && e.status === 404) throw new IntegrationNotFoundError('Esta integração não existe mais.')
    throw e
  }
}

class HttpStatusError extends IntegrationError {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request(path: string, init: RequestInit): Promise<unknown> {
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      signal: AbortSignal.timeout(TIMEOUT_MS),
    })
  } catch {
    throw new OfflineError()
  }
  if (response.status === 401) throw new SessionExpiredError()
  if (response.status === 429) throw new IntegrationError('Muitas tentativas de cadastro. Aguarde um minuto e tente de novo.')
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as { detail?: unknown } | null
    throw new HttpStatusError(response.status, typeof problem?.detail === 'string' ? problem.detail : 'Não foi possível concluir agora. Tente de novo.')
  }
  return response.status === 204 ? null : response.json()
}

function parseIntegration(raw: unknown): Integration {
  const r = (raw ?? {}) as Record<string, unknown>
  if (typeof r.id !== 'string' || typeof r.url !== 'string' || typeof r.createdAt !== 'string')
    throw new IntegrationError('Resposta inesperada do servidor.')
  return { id: r.id, url: r.url, dashboardUrl: safeLink(r.dashboardUrl), createdAt: r.createdAt }
}

// O servidor já só aceita http/https; conferido de novo aqui porque o valor vira um link clicável.
function safeLink(value: unknown): string | null {
  if (typeof value !== 'string') return null
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null
  } catch {
    return null
  }
}
