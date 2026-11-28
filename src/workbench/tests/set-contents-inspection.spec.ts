import { expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { fixture } from './bin-fixture'

test('inspection keeps four buyer-visible configurations of one used set distinct in the same bin', async ({page}, info) => {
  const f = await fixture(page)
  const catalogId = await f.write('catalog', 0, {
    itemType: 'set', itemNumber: 'BOX-' + f.a.code, variant: null, name: 'Fictional used train',
    colorState: 'not-applicable', colorId: null, condition: 'used', completeness: 'complete',
    source: 'Operator inspection', license: 'operator-owned', mapping: 'confirmed'
  })
  const purchaseId = await f.write('receiving/purchases', 0, {
    reference: 'SET-' + f.a.code, source: 'Fictional train purchase', currency: 'EUR',
    orderedQuantity: 4, pendingChargeMinor: null
  })
  const deliveryId = await f.write('receiving/deliveries', 1, {
    purchaseId, catalogId, reference: 'Four train sets', quantity: 4, itemCostMinor: 4200
  })
  const positionId = await f.write('receiving/putaway', 2, {
    purchaseId, catalogId, deliveryId, locationId: f.a.id, confirmedLocationCode: f.a.code, quantity: 4
  })
  const headers = {...f.headers, 'X-BrickVault-Set-Contents':'1', 'X-BrickVault-Pooled-Offers':'1'}
  const offers = '/api/v1/receiving/offers'
  const makeOffer = (expectedRevision:number, positions:string[]) => ({
    commandId: randomUUID(), expectedRevision, purchaseId, catalogId, currency:'EUR', unitPriceMinor:2600,
    setContents:{includesBox:true, includesInstructions:true},
    backing: positions.map(placementId => ({placementId, quantity:1}))
  })
  // Unknown source data must not be interpreted as either an included or a missing accessory.
  const uninspected = await page.request.post(offers, {headers, data:makeOffer(3,[positionId])})
  expect(uninspected.status()).toBe(409)

  const inspected: string[] = []
  let revision = 3
  for (const [box, manual] of [[true,true],[true,false],[false,true],[false,false]]) {
    await page.goto('/#inventory/set-contents/' + positionId)
    const form = page.getByRole('region',{name:'Inspect set contents', exact:true})
    const confirm = form.getByRole('button',{name:'Record set inspection'})
    await expect(form.getByLabel('Original box')).toHaveValue('')
    await expect(form.getByLabel('Printed instructions')).toHaveValue('')
    await form.getByLabel('Quantity inspected').fill('1')
    await form.getByLabel('Confirm bin code').fill(f.a.code)
    await form.getByLabel('Inspection reason').fill('Checked contents of one used train set')
    await form.getByLabel('Original box').selectOption(box ? 'yes' : 'no')
    await expect(confirm).toBeDisabled()
    await form.getByLabel('Printed instructions').selectOption(manual ? 'yes' : 'no')
    if (revision === 3) {
      await page.setViewportSize({width:320,height:800})
      await expect(form.getByLabel('Original box')).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
      await page.screenshot({path:info.outputPath('set-inspection-compact.png')})
    }
    const reply = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/inventory/set-inspections' && response.request().method() === 'POST')
    await confirm.click()
    const saved = await reply
    expect(saved.status()).toBe(200)
    const command = saved.request().postDataJSON()
    const receipt = await saved.json()
    expect(receipt).toMatchObject({kind:'set-inspection',revision:++revision,commandId:command.commandId})
    const result = page.getByRole('region',{name:'Set inspection receipt',exact:true})
    await expect(result).toContainText((box ? 'Box included' : 'No box') + ' · ' + (manual ? 'Instructions included' : 'No instructions'))
    const href = await result.getByRole('link',{name:'Inspect resulting position'}).getAttribute('href')
    expect(href).toMatch(/^#inventory\/[a-f0-9-]{36}$/)
    inspected.push(href!.slice('#inventory/'.length))
    if (revision === 4) {
      const replay = await page.request.post('/api/v1/inventory/set-inspections',{headers,data:command})
      expect(await replay.json()).toEqual(receipt)
      expect((await page.request.get('/api/v1/diagnostics/' + receipt.correlationId)).status()).toBe(426)
      const diagnostic = await page.request.get('/api/v1/diagnostics/' + receipt.correlationId,{headers})
      expect(await diagnostic.json()).toMatchObject({commandId:command.commandId,kind:'set-inspection'})
      await page.setViewportSize({width:1440,height:900})
      await page.screenshot({path:info.outputPath('set-inspection-receipt-desktop.png')})
    }
  }
  expect(new Set(inspected).size).toBe(4)
  const mismatch = await page.request.post(offers,{headers,data:makeOffer(revision,[inspected[0],inspected[3]])})
  expect(mismatch.status()).toBe(409)
  const saved = await page.request.post(offers,{headers,data:makeOffer(revision,[inspected[0]])})
  expect(saved.status()).toBe(200)
  await page.goto('/#receiving/' + purchaseId)
  const stock = page.getByRole('region',{name:'Stock balance'})
  await expect(stock).toContainText('Item cost EUR 42.00')
  const price = page.getByRole('region',{name:'Price and publication'})
  await expect(price).toContainText('Box included · Instructions included')
  await expect(price).toContainText('1 backed')
})
