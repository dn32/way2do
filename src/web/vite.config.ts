/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig, searchForWorkspaceRoot } from 'vite'

const designTokens = fileURLToPath(new URL('../../docs/design', import.meta.url))

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5300,
    strictPort: true,
    // Mesma origem que em produção: o Vite encaminha /api para a API em desenvolvimento (o nginx faz isso no Compose).
    proxy: { '/api': { target: 'http://127.0.0.1:5100', xfwd: true } },
    // Só o projeto e a pasta dos tokens do design (fonte única do CSS). Nada além disso do repositório fica servível
    // pelo servidor de desenvolvimento (ex.: credenciais.local.md, appsettings da API).
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), designTokens] },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
