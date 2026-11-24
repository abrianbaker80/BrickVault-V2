import { expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture } from './bin-fixture'
import { holdPositionLink } from '../src/countLinks'
import type { CountSnapshot } from '../src/api'

test('the owner releases the exact same-project hold from its identified receipt position', async ({page}, info) => {
  const f = await fixture(page)
  const suffix = randomUUID().slice(0,8)
  const positionsInBin = async (bin: string) => {
    const response = await page.request.get('/api/v1/receiving/bins/' + bin + '/counts')
    expect(response.ok()).toBe(true)
    return ((await response.json()) as CountSnapshot).positions
  }
  const plate = (await positionsInBin(f.a.id)).find(p => p.purchaseId === f.paid.purchaseId)!
  const otherBin = (await positionsInBin(f.b.id)).find(p => p.purchaseId === f.paid.purchaseId)!
  const setNumber = '001-' + suffix
  const setId = await f.write('catalog',0,{id:randomUUID(),itemType:'set',itemNumber:setNumber,
    name:'Fictional bridge kit',variant:null,colorState:'not-applicable',colorId:null,condition:'new',completeness:'complete',
    source:'Operator hold qualification',license:'operator-owned',mapping:'confirmed'})
  const purchaseRef = 'KIT-' + suffix
  const kitPurchase = await f.write('receiving/purchases',0,{reference:purchaseRef,source:'Fictional supplier',
    currency:'USD',orderedQuantity:2,pendingChargeMinor:null})
  const kitDelivery = await f.write('receiving/deliveries',1,{purchaseId:kitPurchase,reference:'Box of kits',
    quantity:2,catalogId:setId,itemCostMinor:null})
  const kit = await f.write('receiving/putaway',2,{purchaseId:kitPurchase,deliveryId:kitDelivery,catalogId:setId,
    locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:2})
  const project = 'River crossing'
  const reserve = (revision: number, purchaseId: string, positionId: string, quantity: number) =>
    f.write('receiving/holds',revision,{purchaseId,positionId,quantity,purpose:project,reason:'Project inventory',
      locationId:f.a.id,confirmedLocationCode:f.a.code})
  const retained = await reserve(plate.purchaseRevision,plate.purchaseId,plate.positionId,1)
  const chosen = await reserve(plate.purchaseRevision+1,plate.purchaseId,plate.positionId,2)
  const elsewhere = await reserve(3,kitPurchase,kit,1)
  const dialog = page.getByRole('dialog',{name:'Build holds',exact:true})
  const identity = dialog.getByRole('region',{name:'Selected receipt position'})
  const references = dialog.getByRole('list',{name:'Project holds'}).getByRole('listitem')
  const entry = (id: string) => references.filter({hasText:'Hold ID ' + id})
  const href = (bin: string, id: string) => '/' + holdPositionLink(bin,id)
  await page.goto(href(f.a.id,kit))
  await expect(identity).toContainText(purchaseRef + ' · Box of kits')
  await expect(identity).toContainText('Set · ' + setNumber)
  await expect(identity).toContainText('Fictional bridge kit')
  await expect(identity).toContainText('Position ID ' + kit)
  await expect(entry(elsewhere)).toContainText('1 held · 0 released of 1')
  await expect(references).toHaveCount(1)
  await page.setViewportSize({width:320,height:720})
  await page.goto(href(f.a.id,plate.positionId))
  await expect(identity).toContainText(plate.purchaseReference + ' · ' + plate.deliveryReference)
  await expect(identity).toContainText('Part · BIN-')
  await expect(identity).toContainText(f.item)
  await expect(identity).toContainText('Position ID ' + plate.positionId)
  await expect(references).toHaveCount(2)
  await expect(entry(chosen)).toContainText('2 held · 0 released of 2')
  await expect(entry(retained)).toContainText('1 held · 0 released of 1')
  const select = dialog.getByRole('button',{name:'Release pieces from ' + project + ' · Hold ID ' + chosen,exact:true})
  await select.press('Enter')
  const heading = dialog.getByRole('heading',{name:'Release from ' + project})
  await expect(heading).toBeFocused()
  await dialog.getByRole('button',{name:'Cancel'}).press('Enter')
  await expect(select).toBeFocused()
  await select.press('Enter')
  await page.keyboard.press('Tab')
  await expect(dialog.getByLabel('Quantity to release')).toBeFocused()
  await page.keyboard.type('1')
  await page.keyboard.press('Tab')
  await page.keyboard.type('One plate is required elsewhere')
  const confirm = dialog.getByRole('button',{name:'Confirm release from ' + project + ' · Hold ID ' + chosen,exact:true})
  await confirm.scrollIntoViewIfNeeded()
  const target = dialog.locator('form').getByText('Hold ID ' + chosen,{exact:true})
  await expect(target).toBeInViewport()
  await expect(confirm).toBeInViewport()
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await page.screenshot({path:info.outputPath('identified-hold-release-mobile.png')})
  await audit(page)
  const posted = page.waitForRequest(r => r.method() === 'POST' && r.url().endsWith('/receiving/holds/release'))
  await confirm.press('Enter')
  const body = (await posted).postDataJSON()
  expect(body).toMatchObject({purchaseId:plate.purchaseId,holdId:chosen,quantity:1})
  await expect(dialog.getByText('Released 1 from ' + project + '. Hold ID ' + chosen,{exact:true})).toHaveCount(1)
  await expect(entry(chosen)).toContainText('1 held · 1 released of 2')
  await expect(entry(retained)).toContainText('1 held · 0 released of 1')
  await page.goto(href(f.a.id,kit))
  await expect(entry(elsewhere)).toContainText('1 held · 0 released of 1')
  await expect(references).toHaveCount(1)

  await f.write('receiving/transfers',plate.purchaseRevision+3,{purchaseId:plate.purchaseId,
    sourcePlacementId:otherBin.positionId,sourceLocationId:f.b.id,confirmedSourceLocationCode:f.b.code,
    destinationLocationId:f.empty.id,confirmedDestinationLocationCode:f.empty.code,quantity:otherBin.ledgerQuantity,
    reason:'Clear the second bin after inventory',backing:[]})
  await page.goto(href(f.b.id,otherBin.positionId))
  await expect(identity).toContainText('Position ID ' + otherBin.positionId)
  await expect(identity).toContainText(plate.purchaseReference + ' · ' + plate.deliveryReference)
  await expect(identity).toContainText(f.item)
  await expect(dialog.locator('dt').filter({hasText:'Ledger quantity'}).locator('+ dd')).toHaveText('0')
  await expect(dialog.getByRole('button',{name:'Create build hold'})).toBeDisabled()
})
