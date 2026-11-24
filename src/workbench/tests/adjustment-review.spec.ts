import { expect } from '@playwright/test'
import { audit, fixture } from './bin-fixture'
import { countReceiptLink } from '../src/countLinks'
import { test } from './trusted'

test('a count review identifies protected pieces and both unpublished offers before any loss is posted', async ({ page }, info) => {
  const f = await fixture(page)
  const source = await page.request.get('/api/v1/receiving/bins/' + f.b.id + '/movements')
  const other = (await source.json()).positions[0].layer.placementId
  const sharedOffer = await f.write('receiving/offers', 5, { purchaseId: f.paid.purchaseId, catalogId: f.catalogId,
    unitPriceMinor: 225, backing: [{ placementId: f.position, quantity: 2 }, { placementId: other, quantity: 1 }] })
  await f.write('receiving/transfers', 6, { purchaseId: f.paid.purchaseId, sourcePlacementId: other, sourceLocationId: f.b.id,
    confirmedSourceLocationCode: f.b.code, destinationLocationId: f.a.id, confirmedDestinationLocationCode: f.a.code,
    quantity: 1, reason: 'Same offer also backs a second position in this bin', backing: [{ offerId: sharedOffer, quantity: 1 }] })
  const position = f.position
  const base = { purchaseId: f.paid.purchaseId, positionId: position, locationId: f.a.id, confirmedLocationCode: f.a.code }
  await f.write('receiving/holds', 7, { ...base, purpose: 'Display model', quantity: 1, reason: 'Show reservation separately' })
  const obs = await f.write('receiving/counts', 8, { ...base, countedQuantity: 3, reason: 'Three pieces witnessed in the tray' })
  await page.setViewportSize({ width: 320, height: 720 })
  await page.goto('/' + countReceiptLink(f.a.id, obs))
  const receipt = page.getByRole('dialog', { name: 'Count receipt' })
  await receipt.getByText('Review stock adjustment', { exact: true }).click()
  const review = receipt.getByRole('region', { name: 'Stock adjustment review' })
  await expect(review.getByRole('heading', { name: 'Unpublished offers (2)' })).toBeVisible()
  await expect(review.locator('dd')).toHaveText(['6', '3', '3', '3', '1', '2', '5', 'USD 12.34'])
  await expect(review.getByRole('listitem')).toHaveCount(2)
  await expect(review).toContainText(f.firstOffer)
  await expect(review.getByRole('listitem').filter({ hasText: f.firstOffer })).toContainText('3 backed by this position')
  await expect(review.getByRole('listitem').filter({ hasText: sharedOffer })).toContainText('2 backed by this position')
  await expect(review).toContainText('At least 3 units of offer backing need review.')
  await expect(review.getByRole('link', { name: 'Latest witnessed count' })).toHaveAttribute('href', countReceiptLink(f.a.id, obs))
  expect(await receipt.evaluate(e => e.scrollWidth <= e.clientWidth)).toBe(true)
  await audit(page)
  await review.getByRole('heading', { name: 'Unpublished offers (2)' }).scrollIntoViewIfNeeded()
  await page.screenshot({ path: info.outputPath('adjustment-review-mobile.png') })
  const fresh = await page.request.get('/api/v1/receiving/current?holds=true')
  const facts = (await fresh.json()).purchases.find((p: {id: string}) => p.id === f.paid.purchaseId)
  expect(facts.balances.physicalOnHand).toBe(8)
  expect(facts.balances.reserved).toBe(1)
  expect(facts.offers.map((offer: {backedQuantity: number}) => offer.backedQuantity).sort()).toEqual([3,3])
})

test('offer pages keep their confirmed rows and restart when a newer count changes the case', async ({ page }) => {
  const f = await fixture(page)
  const purchaseId = await f.write('receiving/purchases', 0, { reference: 'OFFERS-' + f.a.code, source: 'Fictional supplier',
    currency: 'EUR', orderedQuantity: 26, pendingChargeMinor: null })
  const deliveryId = await f.write('receiving/deliveries', 1, { purchaseId, reference: 'Uncosted carton', quantity: 26,
    catalogId: f.catalogId, itemCostMinor: null })
  const positionId = await f.write('receiving/putaway', 2, { purchaseId, deliveryId, catalogId: f.catalogId,
    locationId: f.a.id, confirmedLocationCode: f.a.code, quantity: 26 })
  const offerIds: string[] = []
  for (let n = 0; n < 26; n++) {
    offerIds.push(await f.write('receiving/offers', 3 + n, { purchaseId, catalogId: f.catalogId, unitPriceMinor: 100 + n, backing: [{ placementId: positionId, quantity: 1 }] }))
  }
  const base = { purchaseId, positionId, locationId: f.a.id, confirmedLocationCode: f.a.code }
  const opening = await f.write('receiving/counts', 29, { ...base, countedQuantity: 21, reason: 'First observed count' })
  await page.goto('/' + countReceiptLink(f.a.id, opening))
  const receipt = page.getByRole('dialog', { name: 'Count receipt' })
  await receipt.getByText('Review stock adjustment', { exact: true }).click()
  const review = receipt.getByRole('region', { name: 'Stock adjustment review' })
  const heading = review.getByRole('heading', { name: 'Unpublished offers (26)' })
  const rows = review.getByRole('listitem')
  const more = review.getByRole('button', { name: 'More offers' })
  await expect(rows).toHaveCount(25)
  await expect(review).toContainText('Unknown (EUR)')
  const expectedOffers = offerIds.sort().map(id => 'Offer ' + id)
  const firstPage = expectedOffers.slice(0, 25)
  await expect(rows.locator('small')).toHaveText(firstPage)
  const after = (url: URL) => url.pathname.endsWith('/adjustment-review') && url.searchParams.has('after')
  await page.route(after, async route => {
    const reply = await route.fetch()
    await route.fulfill({ json: { ...await reply.json(), complete: false } })
    await reply.dispose()
  }, { times: 1 })
  await more.click()
  await expect(review).toContainText('Incomplete adjustment review')
  await expect(rows).toHaveCount(25)
  await expect(rows.locator('small')).toHaveText(firstPage)
  await review.getByRole('button', { name: 'Retry adjustment review' }).click()
  await expect(rows.locator('small')).toHaveText(expectedOffers.slice(25))
  await expect(heading).toBeFocused()
  await review.getByRole('button', { name: 'Previous offers' }).click()
  await expect(rows).toHaveCount(25)
  const latest = await f.write('receiving/counts', 30, { ...base, countedQuantity: 27, reason: 'One unidentified extra piece' })
  await more.click()
  await expect(review.getByRole('button', { name: 'Read current review' })).toBeVisible()
  await expect(rows).toHaveCount(25)
  await expect(rows.locator('small')).toHaveText(firstPage)
  await review.getByRole('button', { name: 'Read current review' }).click()
  await expect(review.getByRole('link', { name: 'Latest witnessed count' })).toHaveAttribute('href', countReceiptLink(f.a.id, latest))
  await expect(receipt.getByRole('region', { name: 'Open shortage' })).toContainText('5 missing at opening · 0 missing on latest count')
  await expect(receipt.getByText('First observed count', { exact: true })).toBeVisible()
  await expect(review).toContainText('The latest count supports no stock loss.')
  await expect(review.locator('dd')).toHaveText(['26','27','0','26','0','26','26','Unknown (EUR)'])
})
