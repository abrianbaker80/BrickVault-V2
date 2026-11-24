import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture } from './bin-fixture'
import type { BinTransferSnapshot, StockPage } from '../src/api'

type Seed = Awaited<ReturnType<typeof fixture>>
const register = (page: Page) => page.getByRole('region', { name: 'Stock register' })
const detail = (page: Page) => page.locator('[aria-labelledby="position-title"]')
const query = (page: Page) => page.getByRole('searchbox', { name: 'Search stock positions' })
async function search(page: Page, term: string) {
  await query(page).fill(term)
  await query(page).press('Enter')
}
async function totals(page: Page, positions: number, units: number) {
  const metrics = page.getByRole('group', { name: 'Matching inventory totals' })
  await expect(metrics.locator('strong')).toHaveText([String(positions), String(units)])
}
async function positions(page: Page, f: Seed) {
  const response = await page.request.get('/api/v1/receiving/bins/' + f.a.id + '/movements')
  expect(response.ok()).toBe(true)
  const data: BinTransferSnapshot = await response.json()
  return { unknown: data.positions.find(p => p.layer.purchaseId === f.unknown.purchaseId)!.layer,
    paid: data.positions.find(p => p.layer.purchaseId === f.paid.purchaseId)!.layer }
}
async function moreType(f: Seed, itemNumber: string) {
  const catalogId = await f.write('catalog', 0, { itemNumber, itemType: 'set', name: 'Slate boxed set', variant: 'a',
    colorState: 'known', colorId: 7, condition: 'new', completeness: 'complete', source: 'Fictional catalog',
    license: 'operator-owned', mapping: 'confirmed' })
  const purchaseId = await f.write('receiving/purchases', 0, { reference: 'SET-' + f.a.code, source: 'Fictional supplier',
    currency: 'USD', orderedQuantity: 26, pendingChargeMinor: null })
  const deliveryId = await f.write('receiving/deliveries', 1, { purchaseId, reference: 'Set carton', quantity: 26,
    catalogId, itemCostMinor: 9900 })
  for (let i=0; i<26; i++)
    await f.write('receiving/putaway', i+2, { purchaseId, deliveryId, catalogId, quantity: 1,
      locationId: f.empty.id, confirmedLocationCode: f.empty.code })
}

test('Inventory searches across types and bins, pages by position, and retains selected provenance and history', async ({ page }, info) => {
  const f = await fixture(page)
  const itemNumber = 'BIN-' + f.a.code.slice(2)
  const { unknown, paid } = await positions(page, f)
  await moreType(f, itemNumber)
  await page.getByRole('link', { name: 'Inventory' }).click()
  await expect(query(page)).toBeVisible()
  await page.keyboard.press('/')
  await expect(query(page)).toBeFocused()
  await search(page, itemNumber)
  await totals(page, 29, 37)
  await expect(register(page).getByRole('row')).toHaveCount(26)
  const type = register(page).getByRole('combobox', { name: 'Item type', exact: true })
  const bin = register(page).getByRole('combobox', { name: 'Bin', exact: true })
  await type.selectOption('part')
  await totals(page, 3, 11)
  await search(page, f.item)
  await totals(page, 3, 11)
  await search(page, f.a.code)
  await totals(page, 2, 9)
  await search(page, f.unknown.purchase)
  await totals(page, 1, 3)
  await search(page, itemNumber)
  await bin.selectOption(f.a.id)
  await totals(page, 2, 9)
  await register(page).locator('[data-position="'+unknown.placementId+'"]').click()
  await expect(detail(page)).toContainText('Unknown (EUR)')
  await expect(detail(page)).toContainText(f.unknown.purchase)
  await expect(page).toHaveURL(new RegExp('#inventory/' + unknown.placementId + '$'))
  await bin.selectOption('')
  await type.selectOption('set')
  await totals(page, 26, 26)
  const firstIds = await register(page).locator('[data-position]').evaluateAll(nodes => nodes.map(n => n.getAttribute('data-position')))
  await register(page).getByRole('button', { name: 'Next' }).click()
  await expect(register(page)).toContainText('1 shown · Page 2')
  await totals(page, 26, 26)
  expect(firstIds).not.toContain(await register(page).locator('[data-position]').getAttribute('data-position'))
  await expect(detail(page)).toContainText(f.unknown.purchase)
  await register(page).getByRole('button', { name: 'Previous' }).click()
  await expect(register(page)).toContainText('25 shown · Page 1')
  await type.selectOption('part')
  await search(page, paid.deliveryId)
  await totals(page, 2, 8)
  await register(page).locator('[data-position="'+paid.placementId+'"]').click()
  await expect(detail(page)).toContainText(/USD\s*12\.34/)
  await expect(detail(page)).toContainText('8 units')
  await f.revise()
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(detail(page)).toContainText('Recorded at catalog revision 1. Item details show current revision 2.')
  await search(page, itemNumber)
  await totals(page, 3, 11)
  await page.screenshot({ path: info.outputPath('inventory-desktop.png'), fullPage: true })
  await audit(page)

  await register(page).locator('[data-position="'+unknown.placementId+'"]').click()
  await f.write('receiving/transfers', 5, { purchaseId: paid.purchaseId, sourcePlacementId: paid.placementId,
    sourceLocationId: f.a.id, confirmedSourceLocationCode: f.a.code, destinationLocationId: f.b.id,
    confirmedDestinationLocationCode: f.b.code, quantity: 1, reason: 'Unrelated stock movement', backing: [] })
  const destination = await f.write('receiving/transfers', 3, { purchaseId: unknown.purchaseId, sourcePlacementId: unknown.placementId,
    sourceLocationId: f.a.id, confirmedSourceLocationCode: f.a.code, destinationLocationId: f.b.id,
    confirmedDestinationLocationCode: f.b.code, quantity: 3, reason: 'Move counted pouch', backing: [] })
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(detail(page)).toContainText('This position has been fully transferred.')
  const history = detail(page).getByRole('region', { name: 'Position movement history' })
  await expect(history.getByRole('listitem')).toHaveCount(1)
  await expect(history).toContainText('Move counted pouch')
  await expect(history).not.toContainText('Unrelated stock movement')
  await page.reload()
  await expect(detail(page)).toContainText('This position has been fully transferred.')
  await history.getByRole('button', { name: 'Inspect destination position' }).click()
  await expect(page).toHaveURL(new RegExp(destination+'$'))
  await expect(detail(page)).toContainText('Unknown (EUR)')
  await page.setViewportSize({ width: 320, height: 800 })
  const dialog = page.getByRole('dialog', { name: 'Position inspector' })
  await expect(dialog).toBeVisible()
  await expect(history).toContainText('Move counted pouch')
  expect(await dialog.evaluate(e => e.scrollWidth <= e.clientWidth)).toBe(true)
  await page.screenshot({ path: info.outputPath('inventory-mobile-inspector.png') })
  await audit(page)
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await search(page, itemNumber)
  await type.selectOption('part')
  await totals(page, 4, 11)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: info.outputPath('inventory-mobile.png'), fullPage: true })
  await audit(page)

  await search(page, unknown.placementId)
  await totals(page, 1, 0)
  await register(page).locator('[data-position="'+unknown.placementId+'"]').click()
  await expect(dialog).toContainText('This position has been fully transferred.')
})

test('Inventory isolates failures, partial and stale reads from confirmed empty results', async ({ page, context }) => {
  const f = await fixture(page)
  const { paid } = await positions(page, f)
  await page.getByRole('link', { name: 'Inventory' }).click()
  await search(page, paid.deliveryId)
  await totals(page, 2, 8)
  const pageUrl = (url: URL) => url.pathname.endsWith('/inventory/positions') && !url.searchParams.has('positionId')
  const lateTerm = 'LATE-' + randomUUID()
  let release!: () => void
  const gate = new Promise<void>(resolve => { release=resolve })
  const late = page.waitForRequest(r => new URL(r.url()).searchParams.get('q') === lateTerm)
  await page.route(url => pageUrl(url) && url.searchParams.get('q') === lateTerm, async route => {
    await gate
    await route.fulfill({ json: { scope: 'inventory-positions', asOf: new Date().toISOString(), complete: true,
      matchingPositions: 0, matchingUnits: 0, nextAfter: null, rows: [] } })
  }, { times: 1 })
  await search(page, lateTerm)
  await late
  await search(page, paid.placementId)
  await totals(page, 1, 6)
  const settled = page.waitForResponse(r => new URL(r.url()).searchParams.get('q') === lateTerm)
  release()
  await settled
  await expect(register(page).locator('[data-position]')).toHaveAttribute('data-position', paid.placementId)

  await context.setOffline(true)
  await expect(register(page)).toContainText('Showing the last stock read.')
  await expect(page.getByRole('group', { name: 'Last received matching totals' }).locator('strong')).toHaveText(['1','6'])
  await context.setOffline(false)
  await totals(page, 1, 6)
  await page.route(pageUrl, route => route.abort('connectionreset'), {times:1})
  await search(page, 'FAILED-' + f.a.code)
  await expect(register(page)).toContainText('Stock totals are unavailable.')
  await expect(register(page)).not.toContainText('No matching stock positions.')
  const incomplete: StockPage = { scope: 'inventory-positions', asOf:new Date().toISOString(), complete:false,
    matchingPositions:0, matchingUnits:0, nextAfter:null, rows:[] }
  await page.route(pageUrl, route => route.fulfill({json:incomplete}), {times:1})
  await search(page, 'PARTIAL-' + f.a.code)
  await expect(register(page)).toContainText('Partial stock read.')
  await expect(page.getByRole('group', { name:'Matching inventory totals' }).locator('strong')).toHaveText(['—','—'])
  await expect(register(page)).not.toContainText('No matching stock positions.')
  await search(page, 'ABSENT-' + f.a.code)
  await totals(page, 0, 0)
  await expect(register(page)).toContainText('No matching stock positions.')
  await page.goto('/#inventory/' + randomUUID())
  await expect(detail(page)).toContainText('No stock position was found for this ID.')
  await page.route('**/receiving/bins/*/movements', route => route.abort('connectionreset'), {times:1})
  await page.goto('/#inventory/'+paid.placementId)
  await expect(detail(page)).toContainText('Movement history is unavailable.')
  await expect(detail(page)).not.toContainText('No movements recorded for this position.')
  const stamp = detail(page).getByText(/^Position read /)
  const receivedAt = await stamp.textContent()
  await page.route(url => url.pathname.endsWith('/inventory/positions') && url.searchParams.get('positionId') === paid.placementId,
    route => route.fulfill({json:{ ...incomplete, asOf:'2099-01-01T00:00:00Z' }}), {times:1})
  await page.getByRole('button', { name:'Refresh', exact:true }).click()
  await expect(detail(page)).toContainText('The selected position was not returned.')
  await expect(stamp).toHaveText(receivedAt!)
  await expect(detail(page)).toContainText('Showing the last position read.')
})
