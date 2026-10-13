import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

const ENTRA_VARS = ['VITE_ENTRA_CLIENT_ID', 'VITE_ENTRA_AUTHORITY', 'VITE_API_SCOPE'] as const;
const API = 'http://localhost:5139';

export default defineConfig(({ command, mode }) => {
  // A production bundle without Entra settings would have no way to authenticate, so refuse to build one.
  // `vite build --mode development` is still allowed for local experiments.
  if (command === 'build' && mode !== 'development' && mode !== 'test') {
    const env = loadEnv(mode, '.', 'VITE_');
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
      port: 5174,
      proxy: {
        '/api': API,
        '/hubs': { target: API, ws: true },
        // The API's in-memory blob store serves signed upload/download URLs here (Development only).
        '/dev-blobs': API,
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
