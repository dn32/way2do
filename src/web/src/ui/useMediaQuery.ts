import { useSyncExternalStore } from 'react'

interface Store {
  subscribe: (onChange: () => void) => () => void
  getSnapshot: () => boolean
}

// Um MediaQueryList e funções estáveis por consulta: o React não refaz a inscrição a cada render.
const stores = new Map<string, Store>()

function storeFor(query: string): Store {
  let store = stores.get(query)
  if (!store) {
    const media = window.matchMedia(query)
    store = {
      subscribe: (onChange) => {
        media.addEventListener('change', onChange)
        return () => media.removeEventListener('change', onChange)
      },
      getSnapshot: () => media.matches,
    }
    stores.set(query, store)
  }
  return store
}

export function useMediaQuery(query: string): boolean {
  const store = storeFor(query)
  return useSyncExternalStore(store.subscribe, store.getSnapshot)
}

/** Layout de desktop a partir de 900 px; abaixo, o de celular (mobile-first). */
export function useIsDesktop(): boolean {
  return useMediaQuery('(min-width: 900px)')
}
