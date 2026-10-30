import { afterEach, describe, expect, it, vi } from 'vitest'
import { OfflineError, SessionExpiredError } from './errors'
import { IntegrationError, IntegrationNotFoundError, listIntegrations, registerIntegration, removeIntegration } from './integrations'

const item = { id: '6f2f24a6-21f4-469f-b156-7afc6efa3280', url: 'https://c.example.com/…1234', dashboardUrl: 'https://c.example.com/v/ab', createdAt: '2026-10-29T10:00:00Z' }

function respond(response: Response | Error) {
  vi.stubGlobal('fetch', vi.fn(async () => {
    if (response instanceof Error) throw response
    return response
  }))
}

describe('cliente das integrações', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('lista com o link do painel', async () => {
    respond(Response.json([item]))
    expect(await listIntegrations()).toEqual([item])
  })

  it.each([['javascript:alert(1)'], ['data:text/html,oi'], ['não é url']])('descarta link do painel que não é http (%s)', async (dashboardUrl) => {
    respond(Response.json([{ ...item, dashboardUrl }]))
    expect((await listIntegrations())[0].dashboardUrl).toBeNull()
  })

  it('recusa do servidor chega com o motivo dele', async () => {
    respond(Response.json({ title: 'Não foi possível cadastrar a integração.', detail: 'O teste de entrega falhou: o consumidor respondeu 500.' }, { status: 400 }))
    await expect(registerIntegration('https://c.example.com/in/x')).rejects.toThrow('O teste de entrega falhou: o consumidor respondeu 500.')
  })

  it.each([
    ['sem rede', new TypeError('Failed to fetch'), OfflineError],
    ['sessão expirada', new Response(null, { status: 401 }), SessionExpiredError],
    ['limite de tentativas', new Response(null, { status: 429 }), IntegrationError],
  ])('%s', async (_caso, response, error) => {
    respond(response)
    await expect(registerIntegration('https://c.example.com/in/x')).rejects.toBeInstanceOf(error)
  })

  it('404 só significa "integração removida" na remoção', async () => {
    respond(new Response(null, { status: 404 }))
    await expect(removeIntegration('x')).rejects.toBeInstanceOf(IntegrationNotFoundError)
    respond(new Response(null, { status: 404 }))
    const error = await registerIntegration('https://c.example.com/in/x').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(IntegrationError)
    expect(error).not.toBeInstanceOf(IntegrationNotFoundError)
  })
})
