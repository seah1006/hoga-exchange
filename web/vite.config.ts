import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

const apiTarget = 'http://127.0.0.1:5280'

export default defineConfig({
  plugins: [react()],
  test: { environment: 'jsdom' },
  server: {
    host: '127.0.0.1', port: 5173, strictPort: true,
    proxy: { '/api': apiTarget, '/hubs': { target: apiTarget, ws: true } },
  },
})
