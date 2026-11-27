import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture } from './bin-fixture'
import type { BinTransferSnapshot, PositionMovementPage, RecountPreviewPage, StockPage } from '../src/api'

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

test('registered shelf labels find occupied stock and freeze the same recount scope', async ({ page }) => {
  const f = await fixture(page)
  const shelf = f.empty
  const suffix = f.a.code.slice(2)
  const label = 'Tray.' + suffix + '-Back'
  const itemNumberLabel = 'Bin-' + suffix
  const currentCode = 'RACK-' + suffix + '-END'
  const { unknown } = await positions(page, f)
  await moreType(f, 'CASE-' + suffix)
  // This part matches both its item number and the registered label. The 26 sets match only the label.
  const matchingPart = await f.write('receiving/putaway', 3, { purchaseId: f.unknown.purchaseId,
    deliveryId: unknown.deliveryId, catalogId: f.catalogId, locationId: shelf.id, confirmedLocationCode: shelf.code, quantity: 1 })
  const previews = '/api/v1/inventory/recounts/previews'
  const original = { previewId: randomUUID(), scope: 'matching', q: label, itemType: null, locationId: null, positionIds: [] }
  const freezeOriginal = async () => {
    const response = await page.request.post(previews, { headers: f.headers, data: original })
    expect(response.status()).toBe(200)
    return (await response.json() as RecountPreviewPage).total
  }
  expect(await freezeOriginal()).toBe(0)
  await f.write('locations/labels', 1, { locationId: shelf.id, label })
  await f.write('locations/labels', 2, { locationId: shelf.id, label: itemNumberLabel })
  await f.write('locations', 3, { id: shelf.id, code: currentCode, name: shelf.name })
  await f.write('locations/archives', 4, { locationId: shelf.id, confirmedLabel: label, reason: 'Retire this fictional shelf',
    expectedPhysicalQuantity: 27, expectedPositionCount: 27 })
  expect(await freezeOriginal()).toBe(0)

  await page.getByRole('link', { name: 'Inventory' }).click()
  await search(page, label.toLowerCase())
  await totals(page, 27, 27)
  await search(page, label.slice(0, -2))
  await totals(page, 0, 0)
  await search(page, shelf.code.toLowerCase())
  await totals(page, 27, 27)
  await search(page, currentCode.slice(0, -3))
  await totals(page, 27, 27)
  await search(page, f.position)
  await register(page).getByRole('checkbox', { name: 'Select position ' + f.position + ' for recount' }).check()
  await search(page, itemNumberLabel.toLowerCase())
  await totals(page, 30, 38)
  const bin = register(page).getByRole('combobox', { name: 'Bin', exact: true })
  const type = register(page).getByRole('combobox', { name: 'Item type', exact: true })
  await bin.selectOption(f.a.id)
  await totals(page, 2, 9)
  await type.selectOption('set')
  await totals(page, 0, 0)
  await bin.selectOption(shelf.id)
  await totals(page, 26, 26)
  await type.selectOption({ label: 'All types' })
  await totals(page, 27, 27)
  const rows = register(page).locator('[data-position]')
  const visibleIds = () => rows.evaluateAll(nodes => nodes.map(node => node.getAttribute('data-position')!))
  const firstPage = await visibleIds()
  expect(firstPage).toHaveLength(25)
  await register(page).getByRole('button', { name: 'Next' }).click()
  await expect(rows).toHaveCount(2)
  const shown = [...firstPage, ...await visibleIds()]
  expect(new Set(shown).size).toBe(27)
  expect(shown).toContain(matchingPart)

  await register(page).getByRole('button', { name: 'Preview recount' }).press('Enter')
  const review = page.getByRole('dialog', { name: 'Preview recount' })
  await expect(review.getByRole('listitem')).toHaveCount(1)
  await expect(review.getByRole('listitem')).toContainText(f.position)
  const response = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === previews)
  await review.getByRole('combobox', { name: 'Action scope' }).selectOption('matching')
  const frozen: RecountPreviewPage = await (await response).json()
  expect(frozen.total).toBe(27)
  const older = page.waitForResponse(r => new URL(r.url()).pathname === previews + '/' + frozen.previewId)
  await review.getByRole('button', { name: 'Next targets' }).press('Enter')
  const last: RecountPreviewPage = await (await older).json()
  expect(last.nextAfter).toBeNull()
  expect([...frozen.rows, ...last.rows].map(target => target.positionId)).toEqual(shown)
  await f.write('receiving/transfers', 4, { purchaseId: f.unknown.purchaseId, sourcePlacementId: matchingPart,
    sourceLocationId: shelf.id, confirmedSourceLocationCode: label, destinationLocationId: f.b.id,
    confirmedDestinationLocationCode: f.b.code, quantity: 1, reason: 'Move the final piece after freezing the shelf', backing: [] })
  await review.getByRole('button', { name: 'Queue recount' }).press('Enter')
  const receipt = page.getByRole('region', { name: 'Recount receipt', exact: true })
  await expect(receipt).toContainText('27 reviewed · 26 queued')
  await expect(receipt).toContainText('1 unavailable')
  const active = await page.request.get('/api/v1/inventory/positions', { params: { q: label } })
  expect((await active.json() as StockPage).matchingPositions).toBe(26)
  const depleted = await page.request.get('/api/v1/inventory/positions', { params: { positionId: matchingPart } })
  expect((await depleted.json() as StockPage).rows[0].layer.quantity).toBe(0)
})

test('Inventory searches across types and bins, pages by position, and retains selected provenance and history', async ({ page }, info) => {
  const f = await fixture(page)
  const itemNumber = 'BIN-' + f.a.code.slice(2)
  const { unknown, paid } = await positions(page, f)
  await moreType(f, itemNumber)
  const binReads: string[] = []
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/v1/receiving/bins/')) binReads.push(r.url()) })
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
  await expect(detail(page)).toContainText(f.unknown.purchase)
  await detail(page).getByRole('button', { name: 'Transfers only' }).click()
  await expect(detail(page).getByRole('region', { name: 'Position movement history' })).toContainText('No movements recorded for this position.')
  await f.write('receiving/transfers', 5, { purchaseId: paid.purchaseId, sourcePlacementId: paid.placementId,
    sourceLocationId: f.a.id, confirmedSourceLocationCode: f.a.code, destinationLocationId: f.b.id,
    confirmedDestinationLocationCode: f.b.code, quantity: 1, reason: 'Unrelated stock movement', backing: [] })
  const destination = await f.write('receiving/transfers', 3, { purchaseId: unknown.purchaseId, sourcePlacementId: unknown.placementId,
    sourceLocationId: f.a.id, confirmedSourceLocationCode: f.a.code, destinationLocationId: f.b.id,
    confirmedDestinationLocationCode: f.b.code, quantity: 3, reason: 'Move counted pouch', backing: [] })
  const scoped = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/inventory/positions/' + unknown.placementId + '/movements')
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  const movementResponse = await scoped
  expect(movementResponse.ok()).toBe(true)
  const received: PositionMovementPage = await movementResponse.json()
  expect(received.positionId).toBe(unknown.placementId)
  expect(received.rows.map(m => m.reason)).toEqual(['Move counted pouch'])
  expect(new URL(movementResponse.url()).searchParams.get('limit')).toBe('25')
  await expect(detail(page)).toContainText('No units remain in this position.')
  const history = detail(page).getByRole('region', { name: 'Position movement history' })
  await expect(history.getByRole('listitem')).toHaveCount(1)
  await expect(history).toContainText('Move counted pouch')
  await expect(history).not.toContainText('Unrelated stock movement')
  await page.reload()
  await expect(detail(page)).toContainText('No units remain in this position.')
  await detail(page).getByRole('button', { name: 'Transfers only' }).click()
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
  await expect(dialog).toContainText('No units remain in this position.')
  expect(binReads).toEqual([])
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
  await page.route('**/inventory/positions/*/movements?*', route => route.abort('connectionreset'), {times:1})
  await page.goto('/#inventory/'+paid.placementId)
  await detail(page).getByRole('button', { name: 'Transfers only' }).click()
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


test('movement pages keep the confirmed cursor and timestamp when a bounded history read fails', async ({ page }, info) => {
  const f = await fixture(page)
  const purchaseId = await f.write('receiving/purchases', 0, { reference:'HISTORY-' + f.a.code,
    source:'Fictional history test', currency:'USD', orderedQuantity:27, pendingChargeMinor:null })
  const deliveryId = await f.write('receiving/deliveries', 1, { purchaseId, reference:'Counted packages', quantity:27,
    catalogId:f.catalogId, itemCostMinor:null })
  const source = await f.write('receiving/putaway', 2, { purchaseId, deliveryId, catalogId:f.catalogId, locationId:f.a.id,
    confirmedLocationCode:f.a.code, quantity:27 })
  const ids:string[] = []
  for (let n=1; n<=27; n++)
    ids.push(await f.write('receiving/transfers', n+2, { purchaseId, sourcePlacementId:source, sourceLocationId:f.a.id,
      confirmedSourceLocationCode:f.a.code, destinationLocationId:f.b.id, confirmedDestinationLocationCode:f.b.code,
      quantity:1, reason:'Counted move ' + n, backing:[] }))

  const path = '/api/v1/inventory/positions/' + source + '/movements'
  const first = page.waitForResponse(r => new URL(r.url()).pathname === path)
  await page.goto('/#inventory/' + source)
  await detail(page).getByRole('button', { name: 'Transfers only' }).click()
  const initial: PositionMovementPage = await (await first).json()
  expect(initial.scope).toBe('position-movements')
  expect(initial.rows.map(m => m.id)).toEqual(ids.slice(2).reverse())
  expect(initial.nextAfter).toBe(ids[2])
  const panel = detail(page).getByRole('region', {name:'Position movement history'})
  await expect(detail(page)).toContainText('No units remain in this position.')
  await expect(panel.getByRole('listitem')).toHaveCount(25)
  const stamp = panel.getByText(/^Movement read /)
  const timestamp = await stamp.textContent()
  const nextPage = (u: URL) => u.pathname === path && u.searchParams.get('after') === ids[2] && u.searchParams.get('limit') === '25'
  await page.route(nextPage, r => r.abort('connectionreset'), {times:1})
  const older = panel.getByRole('button', {name:'Older movements'})
  await older.focus()
  await page.keyboard.press('Enter')
  const retry = panel.getByRole('button', {name:'Retry movement page 2'})
  await expect(retry).toBeFocused()
  await expect(panel).toContainText('Showing the last movement read.')
  await expect(panel).toContainText('25 shown · Movement page 1')
  await expect(stamp).toHaveText(timestamp!)
  await expect(panel.getByRole('listitem')).toHaveCount(25)

  const unrelated = { ...initial.rows[0], sourcePlacementId:randomUUID(), destinationPlacementId:randomUUID(), reason:'Unrelated row' }
  await page.route(nextPage, r => r.fulfill({json:{...initial, rows:[unrelated]}}), {times:1})
  await retry.click()
  await expect(panel).toContainText('could not verify the server response')
  await expect(panel).not.toContainText('Unrelated row')
  await expect(stamp).toHaveText(timestamp!)
  await page.route(nextPage, r => r.fulfill({json:{...initial, positionId:randomUUID(), rows:[]}}), {times:1})
  const wrongPosition = page.waitForResponse(r => nextPage(new URL(r.url())))
  await retry.click()
  await wrongPosition
  await expect(retry).toBeFocused()
  await expect(panel).toContainText('25 shown · Movement page 1')
  const real = page.waitForResponse(r => nextPage(new URL(r.url())))
  await page.keyboard.press('Enter')
  const last: PositionMovementPage = await (await real).json()
  expect(last.rows.map(m => m.id)).toEqual(ids.slice(0,2).reverse())
  expect(last.nextAfter).toBeNull()
  await expect(panel).toContainText('2 shown · Movement page 2')
  await expect(panel.getByRole('heading', {name:'Movement history'})).toBeFocused()
  await expect(older).toBeDisabled()
  await panel.screenshot({ path: info.outputPath('inventory-movement-history.png') })
  await panel.getByRole('button', {name:'Newer movements'}).focus()
  await page.keyboard.press('Enter')
  await expect(panel).toContainText('25 shown · Movement page 1')
  await expect(panel.getByRole('listitem').first()).toContainText('Counted move 27')
})
