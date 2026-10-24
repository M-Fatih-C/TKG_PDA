import { defineConfig, devices } from '@playwright/test'

// Real-browser acceptance runs against a real ASP.NET Core API + throwaway PostgreSQL 18
// cluster provisioned by e2e/global-setup.ts. The Vite dev server proxies /api to that API.
const API_URL = 'http://127.0.0.1:5099'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 45_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  use: {
    baseURL: 'http://127.0.0.1:4173/admin/',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], channel: 'chromium' } }],
  webServer: {
    command: 'npm run dev',
    url: 'http://127.0.0.1:4173/admin/',
    reuseExistingServer: false,
    timeout: 60_000,
    env: { TKG_API_TARGET: API_URL },
  },
})
