/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig, searchForWorkspaceRoot } from 'vite'
import { VitePWA } from 'vite-plugin-pwa'

const designTokens = fileURLToPath(new URL('../../docs/design', import.meta.url))

export default defineConfig({
  plugins: [
    react(),
    // Service Worker do app shell (escopo, seção 10): o app ABRE sem rede. Só HTML, JS, CSS e ícones do build; /api
    // nunca passa pelo cache (os dados offline vêm do IndexedDB). Versão nova espera o usuário clicar em "Atualizar".
    VitePWA({
      registerType: 'prompt',
      injectRegister: false, // registro por módulo (virtual:pwa-register), sem script inline: compatível com a CSP
      includeAssets: ['favicon.svg', 'apple-touch-icon.png'],
      manifest: {
        name: 'Way2Do',
        short_name: 'Way2Do',
        description: 'Lista de tarefas que funciona sem internet e sincroniza sozinha.',
        lang: 'pt-BR',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        background_color: '#EEF0F3',
        theme_color: '#2563EB',
        icons: [
          { src: 'icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: 'icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,webmanifest}'],
        navigateFallback: 'index.html',
        navigateFallbackDenylist: [/^\/api\//],
        cleanupOutdatedCaches: true,
      },
    }),
  ],
  server: {
    port: 5300,
    strictPort: true,
    // Mesma origem que em produção: o Vite encaminha /api para a API em desenvolvimento (o nginx faz isso no Compose).
    proxy: { '/api': { target: 'http://127.0.0.1:5100', xfwd: true } },
    // Só o projeto e a pasta dos tokens do design (fonte única do CSS). Nada além disso do repositório fica servível
    // pelo servidor de desenvolvimento (ex.: credenciais.local.md, appsettings da API).
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), designTokens] },
  },
  // `npm run preview` serve o build de produção (com o Service Worker) na mesma porta e com o mesmo proxy.
  preview: { port: 5300, strictPort: true },
  test: {
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
