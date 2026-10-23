/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// Development: the React dev server forwards /api calls to the backend (VITE_DEV_PROXY_TARGET), so the browser
// only talks to its own origin and the backend needs no CORS entry. Production builds call VITE_API_BASE_URL directly.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  const proxyTarget = env.VITE_DEV_PROXY_TARGET;

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      proxy: proxyTarget
        ? {
            '/api': {
              target: proxyTarget,
              changeOrigin: true,
              // The ASP.NET Core development certificate is trusted by Windows, not by Node.js. Only the local proxy
              // skips the check; the browser still uses the dev server over its own origin.
              secure: env.VITE_DEV_PROXY_VERIFY_TLS === 'true',
            },
          }
        : undefined,
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      unstubGlobals: true,
    },
  };
});
