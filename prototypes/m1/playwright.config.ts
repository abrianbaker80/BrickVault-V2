import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: 0,
  workers: 3,
  fullyParallel: true,
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:49312', viewport: { width: 1440, height: 900 }, headless: true },
  webServer: {
    command: 'npm run preview -- --port 49312 --strictPort',
    url: 'http://127.0.0.1:49312',
    reuseExistingServer: false,
    timeout: 45_000
  }
})
