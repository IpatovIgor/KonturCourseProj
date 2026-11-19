import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

function forwardHubToken(request) {
  const url = new URL(request.url, 'http://localhost')
  const tokens = url.searchParams.getAll('access_token')
  if (!tokens.length) return

  // Vite includes the proxied URL in diagnostics. Forward credentials as a header.
  url.searchParams.delete('access_token')
  request.url = url.pathname + url.search
  if (!request.headers.authorization && tokens.length === 1 && /^[\w.-]+$/.test(tokens[0])) {
    request.headers.authorization = 'Bearer ' + tokens[0]
  }
}

export default defineConfig(({ mode }) => {
  const { API_PROXY_TARGET, NOTIFICATIONS_PROXY_TARGET } = loadEnv(mode, process.cwd(), ['API_', 'NOTIFICATIONS_'])
  const notifications = { target: NOTIFICATIONS_PROXY_TARGET || 'http://localhost:5162', changeOrigin: true }
  const proxy = {
    '^/api/notifications(?:/|\\?|$)': notifications,
    '^/hubs/notifications(?:/|\\?|$)': { ...notifications, ws: true, bypass: forwardHubToken },
    '/api': { target: API_PROXY_TARGET || 'http://localhost:5160', changeOrigin: true },
  }
  return {
    plugins: [react()],
    server: { port: 5173, strictPort: true, proxy },
    preview: { proxy },
  }
})
