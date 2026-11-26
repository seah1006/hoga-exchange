import react from '@vitejs/plugin-react'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

export default defineConfig(({ mode }) => {
  const apiTarget = loadEnv(mode, '.', 'HOGA_VITE_').HOGA_VITE_API_TARGET || 'http://127.0.0.1:5280'
  return {
    plugins: [react()],
    test: { environment: 'jsdom' },
    server: {
      host: '127.0.0.1', port: 5173, strictPort: true,
      proxy: { '/api': apiTarget, '/hubs': { target: apiTarget, ws: true } },
    },
  }
})
