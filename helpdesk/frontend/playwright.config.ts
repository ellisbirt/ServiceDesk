import { defineConfig, devices } from '@playwright/test';

// The SPA runs against a mocked API (see e2e/*.spec.ts), so no backend is needed. A dedicated port keeps it clear
// of a dev server that is already running on 5173.
const port = 5174;

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  reporter: 'list',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `npx vite --mode development --port ${port} --strictPort`,
    url: `http://localhost:${port}`,
    reuseExistingServer: false,
    timeout: 60_000,
  },
});
