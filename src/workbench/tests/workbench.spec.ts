import { test, expect, chromium, type Page } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { randomUUID } from 'node:crypto'
import { mkdtempDisposable, mkdir, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { Workspace } from '../src/api'

const password = process.env.BV_OWNER_PASSWORD!
const prefix = () => randomUUID().slice(0, 8)
async function signIn(page: Page, value = password) {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Operator sign-in' })).toBeVisible()
  const field = page.getByLabel('Password')
  await expect(field).toBeVisible()
  await field.fill(value).catch(() => { throw new Error('Could not fill the operator password field.') })
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
}
async function connected(page: Page) {
  await expect(page.getByText('Connected', { exact: true })).toBeVisible()
}
async function snapshot(page: Page): Promise<Workspace> {
  const response = await page.request.get('/api/v1/workspace')
  expect(response.status()).toBe(200)
  return response.json()
}
async function openLocation(page: Page, code: string, name: string) {
  await page.getByRole('link', { name: 'Locations' }).click()
  await page.getByRole('button', { name: 'New location' }).click()
  await page.getByLabel('Location code').fill(code)
  await page.getByLabel('Name', { exact: true }).fill(name)
}
async function noOverflow(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
}
async function accessibility(page: Page) {
  const issues = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
  expect(issues.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) }))).toEqual([])
}

test('private sign-in denies wrong credentials and anonymous reference writes', async ({ page, baseURL }) => {
  const code = 'DENIED-' + prefix()
  await signIn(page, 'invalid-' + randomUUID())
  await expect(page.getByRole('alert')).toContainText('Sign-in failed')
  await expect(page.getByRole('button', { name: 'New catalog item' })).toHaveCount(0)
  expect((await page.request.get('/api/v1/workspace')).status()).toBe(401)
  const write = await page.request.post('/api/v1/locations', {
    headers: { Origin: baseURL! },
    data: { commandId: randomUUID(), expectedRevision: 0, code, name: 'Denied reference' }
  })
  expect(write.status()).toBe(401)
  await signIn(page)
  await connected(page)
  expect((await snapshot(page)).locations.some(row => row.code === code)).toBe(false)
  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByLabel('Password')).toBeVisible()
  expect((await page.request.get('/api/v1/workspace')).status()).toBe(401)
})

test('two independent sessions see reference values, source rights, audit and a worker diagnostic', async ({ page, browser, baseURL }, info) => {
  const other = await browser.newContext({ baseURL, ignoreHTTPSErrors: true })
  try {
    const reader = await other.newPage()
    await signIn(page)
    await connected(page)
    await reader.goto('/')
    await expect(reader.getByLabel('Password')).toBeVisible()
    await signIn(reader)
    await connected(reader)
    const key = prefix()
    const number = '0007-' + key
    const name = 'Test tile ' + key
    await page.getByRole('button', { name: 'New catalog item' }).click()
    await page.getByLabel('Item number').fill(number)
    await page.getByLabel('Name', { exact: true }).fill(name)
    await page.getByLabel('Variant (optional)').fill('A')
    await page.getByLabel('Color state').selectOption('known')
    await page.getByLabel('Color ID').fill('0')
    await page.getByLabel('Condition').selectOption('used')
    await page.getByLabel('Catalog source').fill('Fictional operator observation')
    await page.getByLabel('Catalog mapping').selectOption('ambiguous')
    await page.getByRole('button', { name: 'Save catalog item' }).click()
    await expect(page.getByText('Saved ' + name + ' · revision 1.')).toBeVisible()
    await reader.getByRole('button', { name: 'Refresh' }).click()
    await reader.getByRole('searchbox').fill(number)
    const item = reader.getByRole('button', { name: 'Inspect ' + name, exact: true })
    await item.click()
    const details = reader.getByRole('complementary', { name })
    for (const value of [number, 'ID 0', 'Ambiguous', 'Blocked', 'Fictional operator observation'])
      await expect(details.getByText(value, { exact: true })).toBeVisible()
    await reader.screenshot({ path: info.outputPath('catalog-inspector.png') })
    await reader.keyboard.press('Escape')
    await expect(item).toBeFocused()
    const locationName = 'Fictional shelf ' + key
    await openLocation(page, 'TEST-' + key, locationName)
    await page.getByRole('button', { name: 'Save location' }).click()
    await expect(page.getByText('Saved ' + locationName + ' · revision 1.')).toBeVisible()
    await reader.reload()
    await reader.getByRole('link', { name: 'Locations' }).click()
    await reader.getByRole('button', { name: 'Inspect ' + locationName, exact: true }).click()
    await expect(reader.getByRole('complementary', { name: locationName })).toContainText(('test-' + key).toUpperCase())
    await reader.keyboard.press('Escape')
    await reader.getByRole('link', { name: 'Activity' }).click()
    await reader.getByRole('button', { name: 'Inspect command for ' + locationName + ' revision 1', exact: true }).click()
    await expect(reader.getByText('Command ID', { exact: true })).toBeVisible()
    await expect(reader.getByRole('region', { name: 'Worker' })).toContainText('Last heartbeat')
    const stored = await snapshot(reader)
    const location = stored.locations.find(r => r.name === locationName)!
    const audit = stored.audit.filter(row => row.entityId === location.id)
    expect(audit).toHaveLength(1)
    await expect(page.getByRole('status').filter({ hasText: 'Saved ' + locationName })).toContainText(audit[0].correlationId)
    await expect(reader.getByLabel('Correlation ID')).toHaveValue(audit[0].correlationId)
    await expect(reader.getByRole('region', { name: 'Command diagnostic' })).toContainText(audit[0].commandId)
    await reader.getByLabel('Correlation ID').fill(randomUUID())
    await reader.getByRole('button', { name: 'Look up command' }).click()
    await expect(reader.getByRole('alert')).toContainText('Reference')
    await page.getByRole('button', { name: 'Sign out' }).click()
    await expect(page.getByLabel('Password')).toBeVisible()
    await reader.getByRole('button', { name: 'Refresh' }).click()
    await connected(reader)
  } finally { await other.close() }
})

test('a committed command with a lost response survives reload and replays exactly once', async ({ page, context }) => {
  await signIn(page)
  await connected(page)
  const key = prefix()
  const name = 'Recovery shelf ' + key
  await openLocation(page, 'RETRY-' + key, name)
  let firstBody = ''
  await page.route('**/api/v1/locations', async route => {
    firstBody = route.request().postData()!
    const committed = await route.fetch()
    expect(committed.ok()).toBe(true)
    await committed.dispose()
    await route.abort('connectionreset')
  }, { times: 1 })
  await page.getByRole('button', { name: 'Save location' }).click()
  const id: string = JSON.parse(firstBody || '{}').commandId ||
    await page.getByRole('region', { name: 'Pending confirmations' }).locator('small').innerText()
  const recovery = page.getByRole('region', { name: 'Pending confirmations' })
  await expect(recovery).toContainText(id)
  await expect(page.getByText('Disconnected', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New location' })).toBeDisabled()
  expect(await page.evaluate(() => Object.entries(localStorage).filter(([k]) => k.startsWith('brickvault.')).every(([,v]) =>
    !/csrf|password|authorization|cookie/i.test(v)))).toBe(true)
  await page.reload()
  await expect(recovery).toContainText(id)
  await connected(page)
  await expect(page.getByRole('button', { name: 'New location' })).toBeDisabled()
  const replay = page.waitForRequest(req => req.url().endsWith('/api/v1/locations') && req.method() === 'POST')
  await recovery.getByRole('button', { name: 'Retry command' }).click()
  expect((await replay).postData()).toBe(firstBody)
  await expect(page.getByText('Saved ' + name + ' · revision 1.')).toBeVisible()
  await expect(recovery).toHaveCount(0)
  const result = await snapshot(page)
  expect(result.locations.filter(row => row.name === name)).toHaveLength(1)
  expect(result.audit.filter(row => row.commandId === id)).toHaveLength(1)
  expect(await page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('brickvault.')))).toHaveLength(0)
  await context.setOffline(true)
  await expect(page.getByText('Disconnected', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New location' })).toBeDisabled()
  await context.setOffline(false)
  await connected(page)
  await expect(page.getByRole('button', { name: 'New location' })).toBeEnabled()
})

test('320px register and dialogs support keyboard navigation and accessible controls', async ({ page }, info) => {
  await page.setViewportSize({ width: 320, height: 680 })
  await signIn(page)
  await connected(page)
  await noOverflow(page)
  await accessibility(page)
  await page.keyboard.press('/')
  await expect(page.getByRole('searchbox')).toBeFocused()
  const add = page.getByRole('button', { name: 'New catalog item' })
  await add.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByLabel('Item number')).toBeFocused()
  await noOverflow(page)
  await accessibility(page)
  await page.screenshot({ path: info.outputPath('catalog-mobile-form.png') })
  await page.keyboard.press('Escape')
  await expect(add).toBeFocused()
  await page.getByRole('link', { name: 'Activity' }).click()
  await noOverflow(page)
  await accessibility(page)
})

test('a delayed initial read and a lost service connection cannot appear as a confirmed zero', async ({ page }) => {
  let release: () => void = () => {}
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route('**/api/v1/workspace', async route => {
    await gate
    await route.continue()
  }, { times: 1 })
  await signIn(page)
  await expect(page.getByText('Loading workspace…')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Catalog register' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'New catalog item' })).toBeDisabled()
  release()
  await connected(page)
  await page.route('**/api/v1/workspace', route => route.abort('connectionrefused'), { times: 1 })
  // The browser still reports online when only the private service goes away.
  await expect(page.getByText('Disconnected', { exact: true })).toBeVisible({ timeout: 22000 })
  await expect(page.getByRole('alert')).toContainText('could not be reached')
  await expect(page.getByRole('button', { name: 'New catalog item' })).toBeDisabled()
  await expect(page.getByText(/Last read /)).toBeVisible()
  await page.getByRole('button', { name: 'Refresh' }).click()
  await connected(page)
})

test('the Workbench reflows at native 150 percent browser zoom', async ({ baseURL }, info) => {
  await using scratch = await mkdtempDisposable(join(tmpdir(), 'brickvault-workbench-zoom-'))
  const extension = join(scratch.path, 'extension')
  await mkdir(extension)
  await writeFile(join(extension, 'manifest.json'), JSON.stringify({
    manifest_version: 3, name: 'Workbench zoom test', version: '1.0',
    permissions: ['tabs'], background: { service_worker: 'zoom.js' }
  }))
  await writeFile(join(extension, 'zoom.js'), 'chrome.runtime.onInstalled.addListener(() => {})')
  const context = await chromium.launchPersistentContext(join(scratch.path, 'profile'), {
    baseURL, channel: 'chromium', viewport: null, ignoreHTTPSErrors: true,
    args: ['--window-size=1280,720', '--load-extension=' + extension, '--disable-extensions-except=' + extension]
  })
  try {
    const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker')
    const page = await context.newPage()
    await signIn(page)
    await connected(page)
    await page.screenshot({ path: info.outputPath('catalog-desktop.png') })
    const zoom = await worker.evaluate(async origin => {
      const tabs = (globalThis as typeof globalThis & { chrome: { tabs: {
        query(options: object): Promise<{id: number; url?: string}[]>
        setZoom(id: number, factor: number): Promise<void>
        getZoom(id: number): Promise<number>
      } } }).chrome.tabs
      const match = (await tabs.query({})).find(tab => tab.url?.startsWith(origin + '/'))!
      await tabs.setZoom(match.id, 1.5)
      return tabs.getZoom(match.id)
    }, baseURL!)
    expect(zoom).toBe(1.5)
    await page.waitForFunction(() => window.devicePixelRatio >= 1.49 && innerWidth < 930)
    await noOverflow(page)
    const native = await context.newCDPSession(page)
    try {
      const capture = await native.send('Page.captureScreenshot', { format: 'png', fromSurface: true, captureBeyondViewport: false })
      const png = Buffer.from(capture.data, 'base64')
      const contentWidth = await page.evaluate(() => innerWidth)
      expect(Math.abs(png.readUInt32BE(16) - contentWidth * zoom)).toBeLessThan(4)
      await writeFile(info.outputPath('catalog-150pct.png'), png)
    } finally { await native.detach() }
    await page.getByRole('button', { name: 'New catalog item' }).click()
    await expect(page.getByLabel('Item number')).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(page.getByRole('button', { name: 'New catalog item' })).toBeFocused()
  } finally { await context.close() }
})
