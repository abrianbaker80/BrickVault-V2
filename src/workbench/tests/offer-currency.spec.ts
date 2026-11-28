import { expect, type Page } from '@playwright/test'
import { audit, fixture, purchases } from './bin-fixture'
import { test } from './trusted'

const offersPath = '/api/v1/receiving/offers'
const pending = (page: Page) => page.getByRole('region', { name: 'Pending confirmations' })
const retryResponse = (page: Page) => page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === offersPath)

async function chooseStock(page: Page, purchaseId: string, catalogId: string, bin: string, delivery: string) {
  await page.goto('/#receiving/' + purchaseId)
  await page.getByRole('button', { name: 'Prepare offer', exact: true }).click()
  await page.getByLabel('Offer item').selectOption(catalogId)
  await page.getByLabel('Back units from ' + bin + ' · ' + delivery).fill('1')
}

test('an operator prices a draft in a different currency and recovers the exact price after a lost confirmation', async ({ page }, info) => {
  const f = await fixture(page)
  const purchaseId = await f.write('receiving/purchases', 0, { reference: 'GIFT-' + f.a.code, source: 'Fictional gift',
    currency: 'EUR', orderedQuantity: 2, pendingChargeMinor: null })
  const delivery = 'Gift receipt'
  const deliveryId = await f.write('receiving/deliveries', 1, { purchaseId, catalogId: f.catalogId, reference: delivery, quantity: 2, itemCostMinor: 0 })
  await f.write('receiving/putaway', 2, { purchaseId, deliveryId, locationId: f.a.id, confirmedLocationCode: f.a.code, catalogId: f.catalogId, quantity: 2 })
  await chooseStock(page, purchaseId, f.catalogId, f.a.code, delivery)
  const currency = page.getByLabel('Selling currency'), price = page.getByLabel('Manual unit price')
  const save = page.getByRole('button', { name: 'Save unpublished offer' })
  await expect(currency).toHaveValue('EUR')
  await price.fill('2.50')
  await currency.fill('E1')
  expect(await currency.evaluate(input => (input as HTMLInputElement).validity.patternMismatch)).toBe(true)
  await currency.fill('JPY')
  await expect(price).toBeEmpty()
  await price.fill('0')
  await save.click()
  await expect(page.getByRole('alert')).toContainText('Enter a positive manual price.')
  await price.fill('2.5')
  await save.click()
  await expect(page.getByRole('alert')).toContainText('JPY amount with up to 0 decimal places')
  await currency.fill('kwd')
  await expect(price).toBeEmpty()
  await price.fill('1.234')
  await expect(currency).toHaveValue('KWD')
  await expect(page.getByText('1 unit of backing · 1 eligible unit withheld')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Stock balance' })).toContainText('Item cost EUR 0.00')
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.screenshot({ path: info.outputPath('draft-price-desktop.png'), fullPage: true })
  await page.setViewportSize({ width: 320, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
  await price.focus()
  await expect(page.getByLabel('Manual unit price (KWD)')).toBeFocused()
  await page.screenshot({ path: info.outputPath('draft-price-mobile.png'), fullPage: true })
  await audit(page)

  let original = '', offerId = '', correlation = ''
  await page.route('**' + offersPath, async route => {
    original = route.request().postData()!
    expect(JSON.parse(original)).toMatchObject({ purchaseId, unitPriceMinor: 1234, currency: 'KWD' })
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    const ack = await response.json()
    offerId = ack.entityId
    correlation = ack.correlationId
    await response.dispose()
    await route.abort('connectionreset')
  }, { times: 1 })
  await save.click()
  await expect(pending(page)).toContainText('Unpublished offer')
  await page.reload()
  const replay = retryResponse(page)
  await pending(page).getByRole('button', { name: 'Retry command' }).click()
  const response = await replay
  expect(response.request().postData()).toBe(original)
  expect(await response.json()).toMatchObject({ entityId: offerId, correlationId: correlation })
  await expect(pending(page)).toHaveCount(0)
  await page.reload()
  await expect(page.getByRole('list', { name: 'Unpublished offers' })).toContainText('KWD 1.234 per unit')
  await expect(page.getByRole('region', { name: 'Stock balance' })).toContainText('Item cost EUR 0.00')
  const [purchase] = await purchases(page, [purchaseId])
  expect(purchase.offers).toHaveLength(1)
  expect(purchase.offers[0]).toMatchObject({ id: offerId, unitPriceMinor: 1234, currency: 'KWD', backedQuantity: 1 })
  expect(purchase.balances).toMatchObject({ physicalOnHand: 2, backedQuantity: 1, withheldQuantity: 1, publishedQuantity: 0, knownItemCostMinor: 0 })
})

test('a same-currency draft keeps the original command shape and replays from the previous Workbench journal', async ({ page }) => {
  const f = await fixture(page)
  await chooseStock(page, f.unknown.purchaseId, f.catalogId, f.a.code, f.unknown.delivery)
  await expect(page.getByLabel('Selling currency')).toHaveValue('EUR')
  await page.getByLabel('Manual unit price (EUR)').fill('4.50')
  const saved = retryResponse(page)
  await page.getByRole('button', { name: 'Save unpublished offer' }).click()
  const response = await saved
  expect(response.status()).toBe(200)
  const body = response.request().postDataJSON(), original = response.request().postData()
  expect(body).not.toHaveProperty('currency')
  expect(body.unitPriceMinor).toBe(450)
  const ack = await response.json()
  await expect(page.getByRole('list', { name: 'Unpublished offers' })).toContainText('EUR 4.50 per unit')
  const workspaceId = f.headers['X-Workspace-Id']
  await page.evaluate(({ workspaceId, body }) => {
    localStorage.setItem('brickvault.v2.pending.' + workspaceId + '.' + body.commandId,
      JSON.stringify({ workspaceId, kind: 'offer', body }))
  }, { workspaceId, body })
  await page.reload()
  await expect(pending(page)).toContainText(body.commandId)
  const replay = retryResponse(page)
  await pending(page).getByRole('button', { name: 'Retry command' }).click()
  const result = await replay
  expect(result.request().postData()).toBe(original)
  expect(await result.json()).toMatchObject({ entityId: ack.entityId, correlationId: ack.correlationId })
  await expect(pending(page)).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Stock balance' })).toContainText('Item cost Unknown')
  const [purchase] = await purchases(page, [f.unknown.purchaseId])
  expect(purchase.offers).toHaveLength(1)
  expect(purchase.deliveries[0].itemCostMinor).toBeNull()
  expect(purchase.balances.publishedQuantity).toBe(0)
})
