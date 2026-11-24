import { expect, type Page } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import type { BinStockSnapshot, ReceivingWorkspace } from '../src/api'

async function signIn(page: Page) {
  await page.getByLabel('Password').fill(process.env.BV_OWNER_PASSWORD!)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByText('Connected', { exact: true })).toBeVisible()
}

async function fixture(page: Page) {
  const key = randomUUID().slice(0, 8).toUpperCase()
  await page.goto('/#locations')
  await signIn(page)
  const { workspaceId, csrfToken } = await (await page.request.get('/api/v1/session')).json()
  const headers = { Origin: new URL(page.url()).origin, 'X-CSRF-Token': csrfToken, 'X-Workspace-Id': workspaceId }
  async function write(path: string, expectedRevision: number, fields: object) {
    const response = await page.request.post('/api/v1/' + path, { headers, data: {
      commandId: randomUUID(), expectedRevision, ...fields
    } })
    expect(response.status()).toBe(200)
    return (await response.json()).entityId as string
  }
  async function bin(prefix: string) {
    const code = prefix + '-' + key
    const name = 'Bin ' + code
    return { code, name, id: await write('locations', 0, { code, name }) }
  }
  const a = await bin('A'), b = await bin('B'), empty = await bin('E')
  const fields = { itemNumber: 'BIN-' + key, itemType: 'part', name: 'Slate plate ' + key,
    variant: 'a', colorState: 'known', colorId: 7, condition: 'new', completeness: 'complete',
    source: 'Operator bin qualification', license: 'operator-owned', mapping: 'confirmed' }
  const catalogId = await write('catalog', 0, fields)
  async function shipment(prefix: string, quantity: number, currency: string, itemCostMinor: number | null) {
    const purchase = prefix + '-' + key
    const delivery = 'Box ' + prefix
    const purchaseId = await write('receiving/purchases', 0, { reference: purchase, source: 'Fictional supplier',
      currency, orderedQuantity: quantity, pendingChargeMinor: null })
    const deliveryId = await write('receiving/deliveries', 1, { purchaseId, reference: delivery, quantity, catalogId, itemCostMinor })
    return { purchase, delivery, purchaseId, deliveryId }
  }
  const paid = await shipment('PAID', 8, 'USD', 1234)
  const unknown = await shipment('MIXED', 4, 'EUR', null)
  async function putaway(line: typeof paid, bin: typeof a, quantity: number, revision: number) {
    return write('receiving/putaway', revision, { purchaseId: line.purchaseId, deliveryId: line.deliveryId,
      catalogId, locationId: bin.id, confirmedLocationCode: bin.code, quantity })
  }
  const position = await putaway(paid, a, 6, 2)
  await putaway(paid, b, 2, 3)
  await putaway(unknown, a, 3, 2)
  await write('receiving/offers', 4, { purchaseId: paid.purchaseId, catalogId, unitPriceMinor: 150,
    backing: [{ placementId: position, quantity: 3 }] })
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Inspect ' + a.name })).toBeVisible()
  return { a, b, empty, paid, unknown, item: fields.name, headers, revise: () => write('catalog', 1, {
    ...fields, id: catalogId, name: 'Reidentified plate ' + key
  }) }
}

const stock = (page: Page) => page.getByRole('region', { name: 'Stock in this bin' })
const open = (page: Page, bin: { name: string }) => page.getByRole('button', { name: 'Inspect ' + bin.name }).click()
const endpoint = (bin: { id: string }) => '**/api/v1/receiving/bins/' + bin.id
async function totals(page: Page, values: number[]) {
  await expect(stock(page).locator('dt')).toHaveText(['On hand', 'Backed by offers', 'Unbacked', 'Available to back'])
  await expect(stock(page).locator('dd')).toHaveText(values.map(String))
}
async function audit(page: Page) {
  const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
  expect(scan.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) }))).toEqual([])
}
async function purchases(page: Page, ids: string[]) {
  const response = await page.request.get('/api/v1/receiving')
  expect(response.ok()).toBe(true)
  const snapshot: ReceivingWorkspace = await response.json()
  return snapshot.purchases.filter(p => ids.includes(p.id))
}

test('a selected bin retains separate receipt layers, cost coverage and catalog revision evidence', async ({ page }, info) => {
  const f = await fixture(page)
  const ids = [f.paid.purchaseId, f.unknown.purchaseId]
  const before = await purchases(page, ids)
  expect(before.flatMap(p => p.deliveries.flatMap(d => d.placements))
    .filter(p => p.locationId === f.a.id).map(p => p.quantity).sort()).toEqual([3, 6])
  await open(page, f.a)
  const layers = stock(page).getByRole('listitem')
  await totals(page, [9, 3, 6, 6])
  await expect(layers).toHaveCount(2)
  const known = layers.filter({ hasText: f.paid.purchase }), unknown = layers.filter({ hasText: f.unknown.purchase })
  await expect(known).toContainText(f.paid.delivery)
  await expect(known).toContainText(f.item)
  await expect(known).toContainText('6 in bin')
  await expect(known).toContainText('3 backed · 3 unbacked')
  await expect(known).toContainText(/Whole delivery of 8 · Item cost USD\s*12\.34/)
  await expect(unknown).toContainText(f.unknown.delivery)
  await expect(unknown).toContainText(f.item)
  await expect(unknown).toContainText('3 in bin')
  await expect(unknown).toContainText('Whole delivery of 4 · Item cost Unknown (EUR)')
  await page.screenshot({ path: info.outputPath('selected-bin-desktop.png'), fullPage: true })
  await audit(page)
  await open(page, f.b)
  await totals(page, [2, 0, 2, 2])
  await expect(layers).toHaveCount(1)
  await expect(layers).toContainText(f.paid.purchase)
  await open(page, f.empty)
  await expect(stock(page)).toContainText('No stock recorded in this bin.')
  await expect(layers).toHaveCount(0)
  expect(await purchases(page, ids)).toEqual(before)

  await open(page, f.a)
  await f.revise()
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await totals(page, [9, 3, 6, 0])
  await expect(page.getByRole('heading', { name: f.a.name })).toBeVisible()
  await expect(layers.getByText('Placed at catalog revision 1.', { exact: false })).toHaveCount(2)
  await page.setViewportSize({ width: 320, height: 800 })
  const dialog = page.getByRole('dialog', { name: f.a.name })
  await expect(dialog).toBeVisible()
  await totals(page, [9, 3, 6, 0])
  expect(await dialog.evaluate(n => n.scrollWidth <= n.clientWidth)).toBe(true)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: info.outputPath('selected-bin-mobile.png'), fullPage: true })
  await audit(page)
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Inspect ' + f.a.name })).toBeFocused()
})

test('bin inspection distinguishes pending, partial, failed, stale, wrong-bin and expired reads', async ({ page, context }) => {
  const f = await fixture(page)
  const saved = async (id: string): Promise<BinStockSnapshot> => {
    const result = await page.request.get('/api/v1/receiving/bins/' + id)
    expect(result.ok()).toBe(true)
    return result.json()
  }
  const a = await saved(f.a.id), b = await saved(f.b.id)
  let finish!: () => void
  const gate = new Promise<void>(resolve => { finish = resolve })
  let delivered!: () => void
  const done = new Promise<void>(resolve => { delivered = resolve })
  await page.route(endpoint(f.a), async route => {
    await gate
    try { await route.fulfill({ json: a }) } finally { delivered() }
  }, { times: 1 })
  await open(page, f.a)
  await expect(stock(page)).toContainText('Reading bin stock')
  await open(page, f.b)
  await totals(page, [2, 0, 2, 2])
  finish()
  await done
  await expect(stock(page)).not.toContainText(f.unknown.purchase)
  await expect(page.getByRole('heading', { name: f.b.name })).toBeVisible()

  await page.route(endpoint(f.a), route => route.fulfill({ json: { ...a, layers: [], complete: false,
    physicalOnHand: 0, backedQuantity: 0, unbackedQuantity: 0, freeToBackQuantity: 0 } }), { times: 1 })
  await open(page, f.a)
  await expect(stock(page)).toContainText('Partial bin read')
  await expect(stock(page)).not.toContainText('No stock recorded')
  await expect(stock(page).locator('dd')).toHaveCount(0)
  await page.route(endpoint(f.a), route => route.abort('connectionreset'), { times: 1 })
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(stock(page)).toContainText('Showing the last bin read')
  await expect(stock(page)).toContainText('Partial bin read')
  await page.route('**/api/v1/workspace', async route => {
    const response = await route.fetch()
    const read = await response.json()
    await route.fulfill({ json: { ...read, complete: false, locations: [] } })
    await response.dispose()
  }, { times: 1 })
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(page.getByRole('heading', { name: f.a.name })).toBeVisible()
  await expect(page.getByText('The partial register omits this record.', { exact: false })).toBeVisible()
  await totals(page, [9, 3, 6, 6])
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Inspect ' + f.empty.name })).toBeVisible()

  await page.route(endpoint(f.empty), route => route.fulfill({ json: b }), { times: 1 })
  await open(page, f.empty)
  await expect(stock(page)).toContainText('could not verify the server response')
  await expect(stock(page)).toContainText('Bin stock is unavailable')
  await expect(stock(page).getByRole('listitem')).toHaveCount(0)
  await open(page, f.a)
  await totals(page, [9, 3, 6, 6])
  await context.setOffline(true)
  await expect(stock(page)).toContainText('The workspace is disconnected. Showing the last bin read.')
  await totals(page, [9, 3, 6, 6])
  await context.setOffline(false)
  await expect(page.getByText('Connected', { exact: true })).toBeVisible()
  await expect(stock(page).getByRole('status')).toHaveCount(0)

  expect((await page.request.delete('/api/v1/session', { headers: f.headers })).ok()).toBe(true)
  await open(page, f.b)
  await expect(page.getByRole('heading', { name: 'Operator sign-in' })).toBeVisible()
  await expect(stock(page)).toHaveCount(0)
  await expect(page.getByRole('alert')).toContainText('Your session ended')
  await signIn(page)
  await expect(stock(page)).toHaveCount(0)
  await open(page, f.b)
  await totals(page, [2, 0, 2, 2])
})
