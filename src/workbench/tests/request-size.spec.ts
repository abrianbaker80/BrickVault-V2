import { expect, type Route } from '@playwright/test'
import { test } from './trusted'
import { fixture, open, stock } from './bin-fixture'
import type { BinTransferSnapshot } from '../src/api'

async function rejectPaddedBody(route: Route) {
  const postData = route.request().postData()! + ' '.repeat(16384)
  const result = await route.fetch({ postData, headers: {
    ...route.request().headers(), 'content-length': String(Buffer.byteLength(postData))
  } })
  expect(result.status()).toBe(413)
  expect((await result.json()).code).toBe('request_too_large')
  await route.fulfill({ response: result })
  await result.dispose()
}

test('an authenticated size rejection releases the draft and a lost earlier save is reconciled by command ID', async ({ page }) => {
  const f = await fixture(page)
  const name = 'Oversize recovery ' + f.a.code
  await page.getByRole('button', { name: 'New location' }).click()
  await page.getByLabel('Location code').fill('LIMIT-' + f.a.code)
  await page.getByLabel('Name', { exact: true }).fill(name)
  await page.route('**/api/v1/locations', rejectPaddedBody, { times: 1 })
  const save = page.getByRole('button', { name: 'Save location' })
  await save.click()
  await expect(page.getByText('Connected', { exact: true })).toBeVisible()
  const pending = page.getByRole('region', { name: 'Pending confirmations' })
  await expect(pending).toHaveCount(0)
  await expect(save).toBeEnabled()
  let original = ''
  await page.route('**/api/v1/locations', async route => {
    original = route.request().postData()!
    const result = await route.fetch()
    expect(result.ok()).toBe(true)
    await result.dispose()
    await route.abort('connectionreset')
  }, { times: 1 })
  await save.click()
  await expect(pending).toContainText(name)
  await page.reload()
  await page.route('**/api/v1/locations', rejectPaddedBody, { times: 1 })
  const referenceReads: string[] = []
  page.on('request', req => {
    const url = new URL(req.url())
    if (req.method() === 'GET' && url.pathname.endsWith('/workspace')) referenceReads.push(url.search)
  })
  await pending.getByRole('button', { name: 'Retry command' }).click()
  await expect(page.getByText('Confirmed the original ' + name + '.')).toBeVisible()
  expect(referenceReads).toContain('')
  expect(referenceReads).not.toContain('?audit=all')
  await expect(pending).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'New location' })).toBeEnabled()
  const { audit, locations } = await (await page.request.get('/api/v1/workspace')).json()
  expect(locations.filter((row: { name: string }) => row.name === name)).toHaveLength(1)
  expect(audit.filter((row: { commandId: string }) => row.commandId === JSON.parse(original).commandId)).toHaveLength(1)
})

test('a partial move commits 256 selected offer allocations in a single receipt', async ({ page }) => {
  test.setTimeout(120_000)
  const f = await fixture(page)
  const reference = 'PARTIAL-' + f.a.code
  const purchaseId = await f.write('receiving/purchases', 0, { reference, source: 'Training supplier',
    currency: 'USD', orderedQuantity: 257, pendingChargeMinor: null })
  let revision = 1
  const write = (path: string, fields: object) => f.write('receiving/' + path, revision++, { purchaseId, ...fields })
  const deliveryId = await write('deliveries', { reference: 'Wide allocation', quantity: 257, catalogId: f.catalogId, itemCostMinor: 990 })
  const placementId = await write('putaway', { deliveryId, catalogId: f.catalogId, locationId: f.a.id, confirmedLocationCode: f.a.code, quantity: 257 })
  const offers = []
  for (let i = 0; i < 256; i++)
    offers.push(await write('offers', { catalogId: f.catalogId, unitPriceMinor: 120, backing: [{ placementId, quantity: 1 }] }))
  await open(page, f.a)
  const layer = stock(page).getByRole('list', { name: 'Receipt layers in this bin' }).getByRole('listitem').filter({ hasText: reference })
  await layer.getByRole('button', { name: /Transfer stock/ }).click()
  const form = page.getByRole('dialog', { name: 'Transfer stock' })
  await form.getByRole('combobox', { name: 'Destination bin' }).selectOption(f.empty.id)
  await form.getByLabel('Confirm source bin code').fill(f.a.code)
  await form.getByLabel('Confirm destination bin code').fill(f.empty.code)
  await form.getByLabel('Count moved').fill('256')
  await form.getByLabel('Reason', { exact: true }).fill('Counted 256 backed units')
  const lines = form.getByRole('spinbutton', { name: /Move backed units for offer/ })
  await expect(lines).toHaveCount(256)
  for (const offerId of offers)
    await form.getByLabel('Move backed units for offer ' + offerId, { exact: true }).fill('1')
  await expect(form).toContainText('256 units to move · 256 backed')
  const saved = page.waitForResponse(r => r.url().endsWith('/receiving/transfers') && r.request().method() === 'POST')
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  const response = await saved
  expect(response.ok()).toBe(true)
  expect(Buffer.byteLength(response.request().postData()!)).toBeGreaterThan(16384)
  const receipt = await response.json()
  const source: BinTransferSnapshot = await (await page.request.get('/api/v1/receiving/bins/' + f.a.id + '/movements')).json()
  expect(source.positions.find(row => row.layer.placementId === placementId)?.layer.quantity).toBe(1)
  const destination: BinTransferSnapshot = await (await page.request.get('/api/v1/receiving/bins/' + f.empty.id + '/movements')).json()
  const arrived = destination.positions.find(row => row.layer.placementId === receipt.entityId)!
  expect(arrived.layer.backedQuantity).toBe(256)
  expect(arrived.backing.map(row => row.offerId).sort()).toEqual(offers.sort())
  expect(destination.movements.filter(m => m.commandId === receipt.commandId)).toHaveLength(1)
})
