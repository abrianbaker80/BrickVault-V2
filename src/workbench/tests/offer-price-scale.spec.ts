import { randomUUID } from 'node:crypto'
import { expect, type Page } from '@playwright/test'
import { fixture, purchases } from './bin-fixture'
import { test } from './trusted'
import { legacyPriceCompatible, recordedOfferPrice } from '../src/receiving'

const endpoint = '/api/v1/receiving/offers'
const pending = (page: Page) => page.getByRole('region', { name: 'Pending confirmations' })
const retry = (page: Page) => pending(page).getByRole('button', { name: 'Retry command' })
const reply = (page: Page) => page.waitForResponse(r => r.url().endsWith(endpoint) && r.request().method() === 'POST')

test('historical prices disclose the original integer when its decimal scale is unknown', () => {
  for (const currency of ['HUF', 'IQD', 'ZZZ']) {
    expect(legacyPriceCompatible(currency)).toBe(false)
    expect(recordedOfferPrice({ currency, unitPriceMinor: 10 })).toBe(currency + ' · 10 recorded minor units per unit. Verify the original price.')
  }
  expect(recordedOfferPrice({ currency: 'HUF', unitPriceMinor: 10, priceScaleVerified: true })).toMatch(/HUF\s*0\.10 per unit/)
  expect(recordedOfferPrice({ currency: 'IQD', unitPriceMinor: 1234, priceScaleVerified: true })).toMatch(/IQD\s*1\.234 per unit/)
  expect(recordedOfferPrice({ currency: 'EUR', unitPriceMinor: 10 })).toMatch(/EUR\s*0\.10 per unit/)
})

test('an uncertain legacy HUF command keeps its exact bytes until the server confirms re-entry is needed', async ({ page }) => {
  const f = await fixture(page)
  const purchaseId = await f.write('receiving/purchases', 0, { reference: 'SCALE-' + f.a.code, source: 'Fictional supplier',
    currency: 'HUF', orderedQuantity: 2, pendingChargeMinor: null })
  const deliveryId = await f.write('receiving/deliveries', 1, { purchaseId, reference: 'Original pack', catalogId: f.catalogId, quantity: 2, itemCostMinor: null })
  const placementId = await f.write('receiving/putaway', 2, { purchaseId, deliveryId, catalogId: f.catalogId, locationId: f.a.id, confirmedLocationCode: f.a.code, quantity: 2 })
  const workspaceId = f.headers['X-Workspace-Id'], commandId = randomUUID()
  const body = { commandId, purchaseId, expectedRevision: 3, catalogId: f.catalogId, unitPriceMinor: 10, backing: [{ placementId, quantity: 1 }] }
  const command = { workspaceId, kind: 'offer', body }
  const storagePath = ['brickvault.v2.pending', workspaceId, commandId].join('.')
  const original = JSON.stringify(command)
  await page.evaluate(([path, value]) => localStorage.setItem(path, value), [storagePath, original])
  await page.goto('/#receiving/' + purchaseId)
  await page.reload()
  await expect(pending(page)).toContainText(commandId)
  await page.route('**' + endpoint, r => r.abort('connectionreset'), { times: 1 })
  await retry(page).click()
  await expect(page.getByText('Disconnected', { exact: true })).toBeVisible()
  expect(await page.evaluate(path => localStorage.getItem(path), storagePath)).toBe(original)
  await page.reload()
  const rejected = reply(page)
  await retry(page).click()
  const response = await rejected
  expect(response.request().postData()).toBe(JSON.stringify(body))
  expect(await response.request().headerValue('X-BrickVault-Price-Scale')).toBe('1')
  expect(response.status()).toBe(409)
  expect((await response.json()).code).toBe('offer_price_reentry_required')
  await expect(page.getByRole('alert')).toContainText('Verify the original intended unit price')
  await expect(pending(page)).toHaveCount(0)
  expect((await purchases(page, [purchaseId]))[0].offers).toHaveLength(0)

  await page.getByRole('button', { name: 'Prepare offer', exact: true }).click()
  await page.getByLabel('Offer item').selectOption(f.catalogId)
  await expect(page.getByLabel('Selling currency')).toHaveValue('HUF')
  await page.getByLabel('Manual unit price').fill('10.00')
  await page.getByLabel('Back units from ' + f.a.code + ' · Original pack').fill('1')
  const confirmed = reply(page)
  await page.getByRole('button', { name: 'Save unpublished offer' }).click()
  const saved = await confirmed
  expect(saved.status()).toBe(200)
  expect(saved.request().postDataJSON()).toMatchObject({ currency: 'HUF', unitPriceMinor: 1000 })
  expect(saved.request().postDataJSON().commandId).not.toBe(commandId)
  await expect(page.getByRole('list', { name: 'Unpublished offers' })).toContainText('HUF 10.00 per unit')
  const [current] = await purchases(page, [purchaseId])
  expect(current.offers).toHaveLength(1)
  expect(current.offers[0]).toMatchObject({ currency: 'HUF', unitPriceMinor: 1000, priceScaleVerified: true })
  expect(current.balances).toMatchObject({ physicalOnHand: 2, backedQuantity: 1, publishedQuantity: 0 })
  expect(current.deliveries[0].itemCostMinor).toBeNull()
})
