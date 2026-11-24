import { expect } from '@playwright/test'
import { test } from './trusted'
import { fixture, open, stock } from './bin-fixture'
import { TransferCommand as validTransfer } from '../src/validators.cjs'
import type { BinTransferSnapshot } from '../src/api'

test('Receiving reads an offer across 65 positions and a counted whole move carries 65 distinct offers', async ({ page }) => {
  test.slow()
  const f = await fixture(page)
  const reference = 'LARGE-' + f.a.code
  const purchaseId = await f.write('receiving/purchases', 0, { reference, source: 'Fictional mixed backing',
    currency: 'USD', orderedQuantity: 130, pendingChargeMinor: null })
  let revision = 1
  const write = (path: string, fields: object) => f.write('receiving/' + path, revision++, { purchaseId, ...fields })
  const deliveryId = await write('deliveries', { reference: 'Large box', quantity: 130, catalogId: f.catalogId, itemCostMinor: 1300 })
  const position = (bin: typeof f.a) => write('putaway', { deliveryId, catalogId: f.catalogId, locationId: bin.id,
    confirmedLocationCode: bin.code, quantity: 65 })
  const split = await position(f.a)
  const whole = await position(f.b)
  const primaryOffer = await write('offers', { catalogId: f.catalogId, unitPriceMinor: 239,
    backing: [{ placementId: split, quantity: 65 }, { placementId: whole, quantity: 1 }] })
  for (let i = 0; i < 63; i++)
    await write('transfers', { sourcePlacementId: split, sourceLocationId: f.a.id, confirmedSourceLocationCode: f.a.code,
      destinationLocationId: f.empty.id, confirmedDestinationLocationCode: f.empty.code,
      quantity: 1, reason: 'Separate counted unit', backing: [{ offerId: primaryOffer, quantity: 1 }] })
  const expectedOffers = [primaryOffer]
  for (let i = 0; i < 64; i++)
    expectedOffers.push(await write('offers', { catalogId: f.catalogId, unitPriceMinor: 125,
      backing: [{ placementId: whole, quantity: 1 }] }))

  await page.goto('/#receiving/' + purchaseId)
  const offers = page.getByRole('list', { name: 'Unpublished offers' })
  await expect(offers.locator(':scope > li')).toHaveCount(65)
  const broad = offers.locator(':scope > li').filter({ hasText: /USD\s*2\.39 per unit/ })
  await expect(broad.getByRole('listitem')).toHaveCount(65)
  await expect(broad).toContainText('66 backed')

  await page.setViewportSize({ width: 320, height: 800 })
  await page.getByRole('link', { name: 'Locations' }).click()
  await open(page, f.b)
  const row = stock(page).getByRole('list', { name: 'Receipt layers in this bin' }).getByRole('listitem').filter({ hasText: reference })
  await row.getByRole('button', { name: /Transfer stock/ }).click()
  const form = page.getByRole('dialog', { name: 'Transfer stock' })
  await form.getByRole('combobox', { name: 'Destination bin' }).selectOption(f.a.id)
  await form.getByLabel('Confirm source bin code').fill(f.b.code)
  await form.getByLabel('Confirm destination bin code').fill(f.a.code)
  await form.getByLabel('Count moved').fill('65')
  await form.getByLabel('Reason', { exact: true }).fill('Move the complete counted position')
  const all = form.getByRole('checkbox', { name: /Move the whole position/ })
  await expect(all).not.toBeChecked()
  await expect(form.getByRole('spinbutton', { name: /Move backed units for offer/ })).toHaveCount(65)
  await expect(form).toContainText('65 units in this position · 65 backed across 65 offers')
  await all.check()
  await form.getByLabel('Count moved').fill('64')
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  await expect(form.getByRole('alert')).toContainText('count moved must equal 65 units')
  await form.getByLabel('Count moved').fill('65')
  await expect(form.getByRole('spinbutton', { name: /Move backed units for offer/ })).toHaveCount(0)

  let payload = '', destinationPlacement = ''
  await page.route('**/api/v1/receiving/transfers', async route => {
    payload = route.request().postData()!
    const result = await route.fetch()
    expect(result.ok()).toBe(true)
    destinationPlacement = (await result.json()).entityId
    await result.dispose()
    await route.abort('connectionreset')
  }, { times: 1 })
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  const pending = page.getByRole('region', { name: 'Pending confirmations' })
  await expect(pending).toContainText('Transfer of 65')
  const { moveAllBacking, ...explicit } = JSON.parse(payload)
  expect(moveAllBacking).toBe(true)
  expect(explicit.backing).toEqual([])
  expect(validTransfer({ ...explicit, backing: expectedOffers.map(offerId => ({ offerId, quantity: 1 })) })).toBe(true)
  await page.reload()
  const retry = page.waitForRequest(r => r.url().endsWith('/receiving/transfers') && r.method() === 'POST')
  await pending.getByRole('button', { name: 'Retry command' }).click()
  expect((await retry).postData()).toBe(payload)
  const movement = stock(page).getByRole('list', { name: 'Movements involving this bin' }).getByRole('listitem')
    .filter({ hasText: 'Move the complete counted position' })
  await expect(movement).toHaveCount(1)
  await movement.getByRole('button', { name: 'Inspect destination bin' }).click()
  await expect(stock(page).getByRole('list', { name: 'Receipt layers in this bin' })).toContainText('65 backed · 0 unbacked')
  const result = await page.request.get('/api/v1/receiving/bins/' + f.a.id + '/movements')
  expect(result.ok()).toBe(true)
  const read: BinTransferSnapshot = await result.json()
  const moved = read.positions.find(p => p.layer.placementId === destinationPlacement)!
  expect(moved.layer.quantity).toBe(65)
  expect(moved.backing.every(offer => offer.quantity === 1)).toBe(true)
  expect(moved.backing.map(offer => offer.offerId).sort()).toEqual(expectedOffers.sort())
})
