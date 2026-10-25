import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

const ENTRA_VARS = ['VITE_ENTRA_CLIENT_ID', 'VITE_ENTRA_AUTHORITY', 'VITE_API_SCOPE'] as const;

export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, '.', 'VITE_');
  const retryDelay = Number(env.VITE_QUEUE_RETRY_DELAY_SECONDS ?? 15);
  if (!Number.isFinite(retryDelay) || retryDelay < 0 || env.VITE_QUEUE_RETRY_DELAY_SECONDS?.trim() === '') {
    throw new Error('VITE_QUEUE_RETRY_DELAY_SECONDS must be a non-negative number of seconds.');
  }
  // A production bundle without Entra settings would have no way to authenticate, so refuse to build one.
  // `vite build --mode development` is still allowed for local experiments.
  if (command === 'build' && mode !== 'development' && mode !== 'test') {
    // loadEnv merges .env files with matching VITE_* variables from the process environment.
    const missing = ENTRA_VARS.filter((name) => !env[name]);
    if (missing.length > 0) {
      throw new Error(
        `Production build needs ${missing.join(', ')}. Set them in .env.production.local or the environment ` +
          '(see .env.example), or build with --mode development.',
      );
    }
  }

  return {
    plugins: [react(), tailwindcss()],
    server: {
      port: 5173,
      proxy: {
        '/api': 'http://localhost:5129',
        '/hubs': { target: 'http://localhost:5129', ws: true },
      },
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test-setup.ts'],
      exclude: ['e2e/**', 'node_modules/**', 'dist/**'],
    },
  };
});

