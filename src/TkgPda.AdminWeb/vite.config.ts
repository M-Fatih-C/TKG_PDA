import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  base: process.env.VITE_BASE_URL ?? '/admin/',
  plugins: [react()],
  server: {
    // Bind every interface so a tablet or a second PC on the plant network can
    // reach the admin panel. Vite otherwise listens on localhost only, which
    // makes the page unreachable from any other device even when the API is
    // already bound wide. The /api and /health proxies below stay loopback:
    // the browser calls this origin and Vite forwards, so the admin client
    // needs no CORS origin and never learns the API's address.
    host: true,
    port: 4173,
    strictPort: true,
    proxy: {
      '/api': {
        target: process.env.TKG_API_TARGET ?? 'http://localhost:5000',
        changeOrigin: true,
      },
      '/health': {
        target: process.env.TKG_API_TARGET ?? 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
  preview: {
    host: true,
    port: 4173,
    strictPort: true,
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    exclude: ['e2e/**', 'node_modules/**', 'dist/**'],
    css: true,
  },
})
