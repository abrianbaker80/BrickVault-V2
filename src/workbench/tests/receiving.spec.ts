import { expect, type Page } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'

async function references(page: Page, key: string) {
  const { csrfToken, workspaceId } = await (await page.request.get('/api/v1/session')).json()
  const headers = { Origin: new URL(page.url()).origin, 'X-CSRF-Token': csrfToken, 'X-Workspace-Id': workspaceId }
  async function create(path: string, fields: object) {
    const result = await page.request.post('/api/v1/' + path, { headers, data: {
      ...fields, commandId: randomUUID(), expectedRevision: 0
    } })
    expect(result.ok()).toBe(true)
    return (await result.json()).entityId as string
  }
  const itemFields = { itemNumber: 'PL-' + key, itemType: 'part', name: 'Receiving plate ' + key,
    variant: 'a', colorState: 'known', colorId: 7, condition: 'new', completeness: 'complete', mapping: 'confirmed',
    source: 'Operator receiving qualification', license: 'operator-owned' }
  const item = await create('catalog', itemFields)
  const alternative = await create('catalog', { ...itemFields, itemNumber: 'ALT-' + key })
  async function revise(expectedRevision: number, id = item, condition = 'new') {
    const result = await page.request.post('/api/v1/catalog', { headers, data: { ...itemFields, itemNumber: (id === alternative ? 'ALT-' : 'PL-') + key, id, condition,
      name: 'Reconfirmed plate ' + key, commandId: randomUUID(), expectedRevision } })
    expect(result.ok()).toBe(true)
  }
  const binA = 'B-' + key, binB = 'C-' + key
  return { item, alternative, revise, binA, binB, locationA: await create('locations', { name: 'First storage bin', code: binA }),
    locationB: await create('locations', { name: 'Second storage bin', code: binB }) }
}

async function balance(page: Page, physical: number, unavailable: number, free: number) {
  const panel = page.getByRole('region', { name: 'Stock balance' })
  await expect(panel.locator('dt')).toHaveText(['Physical on hand', 'Unavailable', 'Reserved', 'Free'])
  await expect(panel.locator('dd')).toHaveText([String(physical), String(unavailable), '0', String(free)])
}

test('split receipts, lost confirmation, partial putaway and explicit unpublished backing', async ({ page, context, browser, baseURL }) => {
  const key = randomUUID().slice(0, 8).toUpperCase()
  const purchase = 'UI-PO-' + key
  await page.goto('/')
  await page.getByLabel('Password').fill(process.env.BV_OWNER_PASSWORD!)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByText('Connected', { exact: true })).toBeVisible()
  const { item, alternative, revise, binA, binB, locationA, locationB } = await references(page, key)
  await page.getByRole('link', { name: 'Receiving' }).click()
  await page.getByRole('button', { name: 'New purchase' }).click()
  await page.getByLabel('Purchase reference').fill(purchase)
  await page.getByLabel('Purchase source').fill('Fictional invoice')
  await page.getByLabel('Ordered quantity').fill('12')
  await page.getByLabel('Currency code').fill('USD')
  await page.getByLabel('Unallocated purchase charge').fill('0.60')
  await page.getByRole('button', { name: 'Save purchase' }).click()
  await expect(page.getByRole('heading', { name: purchase })).toBeVisible()

  await page.getByLabel('Delivery reference').fill('Box A')
  await page.getByLabel('Units received').fill('8')
  await page.getByLabel('Item identification').selectOption('identified')
  await page.getByLabel('Received catalog item').selectOption(item)
  await page.getByLabel('Item cost basis').selectOption('known')
  await page.getByLabel('Total item cost').fill('8.00')
  let original = ''
  let firstReceipt = ''
  await page.route('**/api/v1/receiving/deliveries', async route => {
    original = route.request().postData()!
    const result = await route.fetch()
    expect(result.ok()).toBe(true)
    firstReceipt = (await result.json()).correlationId
    await result.dispose()
    await route.abort('connectionreset')
  }, { times: 1 })
  await page.getByRole('button', { name: 'Receive delivery' }).click()
  const pending = page.getByRole('region', { name: 'Pending confirmations' })
  await expect(pending).toContainText('Delivery Box A')
  await expect(page.getByRole('button', { name: 'New purchase' })).toBeDisabled()
  await page.reload()
  await expect(pending).toContainText(JSON.parse(original).commandId)
  const replay = page.waitForRequest(req => req.url().endsWith('/receiving/deliveries') && req.method() === 'POST')
  await pending.getByRole('button', { name: 'Retry command' }).click()
  expect((await replay).postData()).toBe(original)
  await expect(page.getByText('Correlation ID ' + firstReceipt)).toBeVisible()
  await balance(page, 8, 8, 0)
  await page.getByRole('button', { name: 'Put away', exact: true }).click()
  await page.getByLabel('Destination bin').selectOption(locationA)
  await expect(page.getByLabel('Confirm bin code')).toHaveValue('')
  await page.getByLabel('Confirm bin code').fill(binB)
  await page.getByLabel('Count placed').fill('8')
  await page.getByRole('button', { name: 'Confirm putaway' }).click()
  await expect(page.getByRole('alert')).toContainText('Bin code does not match')
  await balance(page, 8, 8, 0)
  await page.getByLabel('Confirm bin code').fill(binA)
  await revise(1)
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(page.getByText(/Received at catalog revision 1\. Current revision 2/)).toContainText('Reconfirmed plate ' + key)
  await expect(page.getByRole('button', { name: 'Confirm putaway' })).toBeDisabled()
  await page.getByRole('checkbox', { name: /checked the physical item against catalog revision 2/ }).check()
  const confirmation = page.waitForRequest(req => req.url().endsWith('/receiving/putaway') && req.method() === 'POST')
  await page.getByRole('button', { name: 'Confirm putaway' }).click()
  expect((await confirmation).postDataJSON().confirmedCatalogRevision).toBe(2)
  await balance(page, 8, 0, 8)

  await page.getByLabel('Delivery reference').fill('Mixed pouch')
  await page.getByLabel('Units received').fill('4')
  await page.getByLabel('Item identification').selectOption('mixed')
  await page.getByLabel('Item cost basis').selectOption('unknown')
  await page.getByRole('button', { name: 'Receive delivery' }).click()
  await balance(page, 12, 4, 8)
  const identify = page.getByLabel('Identified catalog item')
  await identify.selectOption(alternative)
  await page.getByLabel('Destination bin').selectOption(locationB)
  await page.getByLabel('Confirm bin code').fill(binB)
  await page.getByLabel('Count placed').fill('3')
  await revise(1, alternative, 'used')
  const declined = page.waitForResponse(r => r.url().endsWith('/receiving/putaway') && r.status() === 409)
  await page.getByRole('button', { name: 'Confirm putaway' }).click()
  await declined
  await expect(identify).toHaveValue('')
  await balance(page, 12, 4, 8)
  await identify.selectOption(item)
  await page.getByLabel('Destination bin').selectOption(locationB)
  await page.getByLabel('Confirm bin code').fill(binB)
  await page.getByLabel('Count placed').fill('3')
  await page.getByRole('button', { name: 'Confirm putaway' }).click()
  await balance(page, 12, 1, 11)
  const cost = page.getByRole('region', { name: 'Cost coverage' })
  await expect(cost).toContainText('8 of 11')
  await expect(cost).toContainText(/USD\s*8\.60/)
  const lines = page.getByRole('list', { name: 'Deliveries and bin positions' })
  await expect(lines).toContainText('Quarantine1')
  await expect(lines).toContainText('Item cost Unknown')
  await page.getByRole('button', { name: 'Prepare offer', exact: true }).click()
  await page.getByLabel('Offer item').selectOption(item)
  await page.getByLabel('Manual unit price').fill('1.50')
  await page.getByLabel('Back units from ' + binA + ' · Box A').fill('5')
  await page.getByLabel('Back units from ' + binB + ' · Mixed pouch').fill('2')
  await expect(page.getByText('7 units of backing · 4 eligible units withheld')).toBeVisible()
  await context.setOffline(true)
  await expect(page.getByRole('button', { name: 'Save unpublished offer' })).toBeDisabled()
  await context.setOffline(false)
  await page.getByRole('button', { name: 'Save unpublished offer' }).click()
  const offers = page.getByRole('list', { name: 'Unpublished offers' })
  await expect(offers).toContainText(/USD\s*1\.50 per unit/)
  await expect(offers).toContainText('7 backed')
  await expect(offers).toContainText(binA + ' · Box A5')
  await expect(offers).toContainText(binB + ' · Mixed pouch2')
  await balance(page, 12, 1, 11)
  await page.reload()
  await expect(page.getByRole('heading', { name: purchase })).toBeVisible()
  await expect(offers).toContainText('7 backed')
  await expect(page.getByRole('region', { name: 'Price and publication' })).toContainText('Published0')
  await revise(2, item, 'used')
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(offers).toContainText('The backing needs renewed catalog confirmation')
  await expect(lines.getByText('Recorded catalog ' + item + ' · Revision 1.', { exact: false }).first()).toBeVisible()
  await expect(lines.getByText('Recorded catalog ' + item + ' · Revision 2.', { exact: false }).first()).toBeVisible()
  await expect(offers).toContainText('Current catalog (revision 3)')
  await expect(offers).toContainText('used')
  const publication = page.getByRole('region', { name: 'Price and publication' })
  await expect(publication.locator('dt')).toHaveText(['Backed', 'Free units withheld', 'Published'])
  await expect(publication.locator('dd')).toHaveText(['7', '4', '0'])
  const reader = await browser.newContext({ baseURL, viewport: { width: 320, height: 800 } })
  try {
    const narrow = await reader.newPage()
    await narrow.goto(page.url())
    await narrow.getByLabel('Password').fill(process.env.BV_OWNER_PASSWORD!)
    await narrow.getByRole('button', { name: 'Sign in', exact: true }).click()
    await expect(narrow.getByRole('heading', { name: purchase })).toBeVisible()
    await balance(narrow, 12, 1, 11)
    expect(await narrow.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  } finally { await reader.close() }
})

test('purchase selection remains bound to its link across refresh and same-page navigation', async ({ page }) => {
  await page.goto('/#receiving')
  await page.getByLabel('Password').fill(process.env.BV_OWNER_PASSWORD!)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  const session = await (await page.request.get('/api/v1/session')).json()
  async function purchase(reference: string) {
    const response = await page.request.post('/api/v1/receiving/purchases', {
      headers: { 'X-CSRF-Token': session.csrfToken, 'X-Workspace-Id': session.workspaceId, Origin: new URL(page.url()).origin },
      data: { commandId: randomUUID(), expectedRevision: 0, reference, source: 'Fictional selection test',
        currency: 'USD', orderedQuantity: 2, pendingChargeMinor: null }
    })
    expect(response.ok()).toBe(true)
    return (await response.json()).entityId as string
  }
  const key = randomUUID().slice(0,8)
  const first = await purchase('PO-A-' + key)
  const second = await purchase('PO-B-' + key)
  await page.goto('/#receiving/' + first)
  await page.getByRole('button', { name:'Refresh', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'PO-A-' + key })).toBeVisible()
  await page.goto('/#receiving/' + second)
  await expect(page.getByRole('heading', { name: 'PO-B-' + key })).toBeVisible()
  await purchase('000-' + key)
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'PO-B-' + key })).toBeVisible()
  await page.goto('/#receiving/' + randomUUID())
  await expect(page.getByText('The selected purchase is unavailable in this read.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Receive delivery' })).toHaveCount(0)
  await page.getByLabel('Purchase', { exact: true }).selectOption(first)
  await expect(page.getByRole('heading', { name: 'PO-A-' + key })).toBeVisible()
})

async function accessible(page: Page) {
  const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
  expect(scan.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) }))).toEqual([])
}

test('Receiving forms and stock readback are accessible at 320 pixels', async ({ page }) => {
  const purchase = 'UI-A11Y-' + randomUUID().slice(0, 8)
  await page.setViewportSize({ width: 320, height: 800 })
  await page.goto('/')
  await page.getByLabel('Password').fill(process.env.BV_OWNER_PASSWORD!)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await page.getByRole('link', { name: 'Receiving' }).click()
  await page.getByRole('button', { name: 'New purchase' }).click()
  await expect(page.getByLabel('Purchase reference')).toBeFocused()
  await accessible(page)
  await page.getByLabel('Purchase reference').fill(purchase)
  await page.getByLabel('Purchase source').fill('Fictional accessibility qualification')
  await page.getByLabel('Ordered quantity').fill('2')
  await page.getByLabel('Currency code').fill('USD')
  await page.getByRole('button', { name: 'Save purchase' }).click()
  await expect(page.getByRole('heading', { name: purchase })).toBeVisible()
  await page.getByLabel('Delivery reference').fill('Mixed pouch')
  await page.getByLabel('Units received').fill('2')
  await page.getByLabel('Item identification').selectOption('mixed')
  await page.getByLabel('Item cost basis').selectOption('unknown')
  await page.getByRole('button', { name: 'Receive delivery' }).click()
  await balance(page, 2, 2, 0)
  await expect(page.getByRole('list', { name: 'Deliveries and bin positions' })).toContainText('Quarantine2')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await accessible(page)
})
