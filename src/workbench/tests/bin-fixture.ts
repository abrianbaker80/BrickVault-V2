import { expect, type Page } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { randomUUID } from 'node:crypto'
import type { ReceivingWorkspace } from '../src/api'

export async function signIn(page: Page) {
  await page.getByLabel('Password').fill(process.env.BV_OWNER_PASSWORD!)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByText('Connected', { exact: true })).toBeVisible()
}

export async function fixture(page: Page) {
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
  const firstOffer = await write('receiving/offers', 4, { purchaseId: paid.purchaseId, catalogId, unitPriceMinor: 150,
    backing: [{ placementId: position, quantity: 3 }] })
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Inspect ' + a.name })).toBeVisible()
  return { a, b, empty, paid, unknown, catalogId, position, write, firstOffer, secondOffer: () => write('receiving/offers', 5, {
    purchaseId: paid.purchaseId, catalogId, unitPriceMinor: 225, backing: [{ placementId: position, quantity: 2 }]
  }), item: fields.name, headers, revise: () => write('catalog', 1, {
    ...fields, id: catalogId, name: 'Reidentified plate ' + key
  }) }
}

export const stock = (page: Page) => page.getByRole('region', { name: 'Stock in this bin' })
export const open = (page: Page, bin: { name: string }) => page.getByRole('button', { name: 'Inspect ' + bin.name }).click()
export const endpoint = (bin: { id: string }) => '**/api/v1/receiving/bins/' + bin.id + '/movements?*'
export async function totals(page: Page, values: number[]) {
  await expect(stock(page).locator('dt')).toHaveText(['On hand', 'Backed by offers', 'Unbacked', 'Available to back'])
  await expect(stock(page).locator('dd')).toHaveText(values.map(String))
}
export async function audit(page: Page) {
  const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
  expect(scan.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) }))).toEqual([])
}
export async function purchases(page: Page, ids: string[]) {
  const response = await page.request.get('/api/v1/receiving/current?holds=true&losses=true&quarantine=true&recoveries=true&partOuts=true')
  expect(response.ok()).toBe(true)
  const snapshot: ReceivingWorkspace = await response.json()
  return snapshot.purchases.filter(p => ids.includes(p.id))
}
