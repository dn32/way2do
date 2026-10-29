import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { sessionDb } from './db'
import { currentSession, endSession, retryPendingLogout, startSession } from './session'

const ana = { id: '6f2f24a6-21f4-469f-b156-7afc6efa3280', name: 'ana' }

function serverAnswers(...responses: (number | 'offline')[]) {
  const fetchMock = vi.fn(async () => {
    const next = responses.shift() ?? 204
    if (next === 'offline') throw new TypeError('Failed to fetch')
    return new Response(null, { status: next })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('sessão e logout', () => {
  beforeEach(async () => {
    await sessionDb.session.clear()
    await sessionDb.flags.clear()
  })
  afterEach(() => vi.unstubAllGlobals())

  it('logout confirmado pelo servidor não deixa pendência', async () => {
    serverAnswers(204)
    await startSession(ana)

    await endSession()

    expect(await currentSession()).toBeNull()
    expect(await sessionDb.flags.get('pendingLogout')).toBeUndefined()
  })

  it.each([['sem conexão', 'offline' as const], ['502 do proxy com a API fora', 502]])(
    'logout não confirmado (%s) esquece o usuário na hora e deixa a pendência',
    async (_caso, answer) => {
      serverAnswers(answer)
      await startSession(ana)

      await endSession()

      expect(await currentSession()).toBeNull()
      expect(await sessionDb.flags.get('pendingLogout')).toBeDefined()
    },
  )

  it('a pendência é reenviada quando a conexão volta e some com a confirmação', async () => {
    const fetchMock = serverAnswers('offline', 204)
    await startSession(ana)
    await endSession()

    await retryPendingLogout()

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(await sessionDb.flags.get('pendingLogout')).toBeUndefined()
  })

  it('um login novo descarta a pendência: o logout antigo derrubaria a sessão nova', async () => {
    const fetchMock = serverAnswers('offline')
    await startSession(ana)
    await endSession()

    await startSession(ana)
    expect(await sessionDb.flags.get('pendingLogout')).toBeUndefined() // o próprio login já descarta
    await retryPendingLogout()

    expect(fetchMock).toHaveBeenCalledTimes(1) // só o logout original, nunca reenviado
    expect(await sessionDb.flags.get('pendingLogout')).toBeUndefined()
  })
})
