import { useSyncExternalStore } from 'react'

function subscribe(onChange: () => void): () => void {
  window.addEventListener('online', onChange)
  window.addEventListener('offline', onChange)
  return () => {
    window.removeEventListener('online', onChange)
    window.removeEventListener('offline', onChange)
  }
}

/** `navigator.onLine` observado. É só uma dica: o "Offline" do indicador também vem da falha real das requisições. */
export function useBrowserOnline(): boolean {
  return useSyncExternalStore(subscribe, () => navigator.onLine, () => true)
}
