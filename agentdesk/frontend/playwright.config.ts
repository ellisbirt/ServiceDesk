import { defineConfig, devices } from '@playwright/test';

// The SPA runs against a mocked API (see e2e/*.spec.ts), so no backend is needed. Port 5175 keeps it clear of the
// AgentDesk dev server (5174) and the intake frontend's dev server and e2e server (5173/5174).
const port = 5175;

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
