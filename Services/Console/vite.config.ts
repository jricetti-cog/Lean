/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` proxies /api and /openapi to the gateway (LEAN_GATEWAY_URL) and adds the API key from LEAN_API_KEY,
// exactly as nginx does in the console image, so the browser bundle never contains the key.
export default defineConfig(({ mode }) => {
  const env = { ...loadEnv(mode, process.cwd(), ''), ...process.env };
  const target = env.LEAN_GATEWAY_URL ?? 'http://127.0.0.1:5080';
  const headers = { 'X-Api-Key': env.LEAN_API_KEY ?? 'lean-dev-api-key' };
  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': { target, changeOrigin: true, headers },
        '/openapi': { target, changeOrigin: true, headers },
      },
    },
    build: {
      rollupOptions: {
        output: {
          manualChunks: {
            react: ['react', 'react-dom', 'react-router'],
            codemirror: ['codemirror', '@codemirror/state', '@codemirror/view', '@codemirror/language', '@codemirror/lang-python', '@codemirror/legacy-modes/mode/clike'],
            charts: ['lightweight-charts'],
          },
        },
      },
    },
    test: { environment: 'node' },
  };
});
