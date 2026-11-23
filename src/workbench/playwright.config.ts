import { defineConfig } from '@playwright/test'

const origin = new URL(process.env.BV_PUBLIC_ORIGIN || 'https://localhost:49420')
if (origin.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname))
  throw new Error('Workbench browser tests require an isolated loopback HTTPS origin.')
if (!process.env.BV_OWNER_PASSWORD) throw new Error('Set BV_OWNER_PASSWORD for the isolated gateway.')

if (!process.env.BV_LAB_CA || !process.env.NODE_EXTRA_CA_CERTS)
  throw new Error('Set BV_LAB_CA and NODE_EXTRA_CA_CERTS to the private instance CA before starting Playwright.')

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 45_000,
  reporter: 'list',
  use: {
    baseURL: origin.origin,
    viewport: { width: 1440, height: 900 },
    browserName: 'firefox',
    trace: 'off',
    screenshot: 'off',
    video: 'off'
  }
})
