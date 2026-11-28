import { expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { fixture, stock, totals } from './bin-fixture'
import { draftBackingLink, holdPositionLink } from '../src/countLinks'

test('two identical sets can move by their recorded contents and only inspected stock can back offers', async ({page}) => {
  const f = await fixture(page), bin = f.empty
  const name = 'Used train ' + bin.code, reference = 'TRAINS-' + bin.code
  const catalogId = await f.write('catalog', 0, {itemType:'set', itemNumber:bin.code, name, variant:null,
    colorState:'not-applicable', colorId:null, condition:'used', completeness:'complete',
    source:'Operator inspection', license:'operator-owned', mapping:'confirmed'})
  const purchaseId = await f.write('receiving/purchases', 0, {reference, source:'Fictional train purchase',
    currency:'USD', orderedQuantity:2, pendingChargeMinor:null})
  const deliveryId = await f.write('receiving/deliveries', 1, {purchaseId,catalogId,reference:'Train carton',quantity:2,itemCostMinor:4200})
  const location = {locationId:bin.id, confirmedLocationCode:bin.code}
  const positionId = await f.write('receiving/putaway', 2, {purchaseId,catalogId,deliveryId,...location,quantity:2})
  const headers = {...f.headers, 'X-BrickVault-Set-Contents':'1'}
  const rows = stock(page).getByRole('list',{name:'Receipt layers in this bin'}).getByRole('listitem')
  const transfer = page.getByRole('dialog',{name:'Transfer stock',exact:true})

  await page.goto('/#locations/'+bin.id)
  await totals(page,[2,0,2,0])
  await expect(rows).toContainText('Box and instructions unrecorded')
  await expect(rows).toContainText('2 unbacked · 0 available to back')
  await rows.getByRole('button',{name:/Transfer stock/}).click()
  await expect(transfer.getByLabel('Count moved')).toHaveAttribute('max','2')
  await transfer.getByRole('button',{name:'Cancel'}).click()
  await page.goto('/'+holdPositionLink(bin.id,positionId))
  await expect(page.getByText('Available for a new hold',{exact:true}).locator('+ dd')).toHaveText('2')
  await expect(page.getByRole('button',{name:'Create build hold'})).toBeEnabled()
  await page.goto('/'+draftBackingLink(bin.id,positionId))
  await expect(page.getByText('Available for new backing',{exact:true}).locator('+ dd')).toHaveText('0')

  const boxed = 'Box included · No instructions', manual = 'No box · Instructions included'
  let revision = 3
  async function inspect(includesBox:boolean, includesInstructions:boolean) {
    const commandId = randomUUID()
    const write = await page.request.post('/api/v1/inventory/set-inspections',{headers, data:{
      commandId,purchaseId,expectedRevision:revision++,positionId,...location,catalogId,catalogRevision:1,
      quantity:1,setContents:{includesBox,includesInstructions},reason:'Inspected one train set'
    }})
    expect(write.status()).toBe(200)
    const receipt = await page.request.get('/api/v1/inventory/positions/'+positionId+'/set-inspection-receipt?commandId='+commandId,{headers})
    expect(receipt.status()).toBe(200)
    return (await receipt.json()).resultPositionId as string
  }
  const withBoxId = await inspect(true,false)
  expect(await inspect(false,true)).toBe(positionId)
  expect(withBoxId).not.toBe(positionId)
  await page.goto('/#receiving/'+purchaseId)
  await page.getByRole('button',{name:'Prepare offer',exact:true}).click()
  await page.getByLabel('Offer item').selectOption(catalogId)
  const offered = page.getByLabel('Set contents for this offer')
  await expect(offered).toHaveValue('')
  for (const contents of [boxed,manual]) {
    await offered.selectOption({label:contents})
    await expect(page.getByRole('group',{name:'Back from these positions'}).getByRole('spinbutton')).toHaveCount(1)
  }
  await page.goto('/#locations/'+bin.id)
  await totals(page,[2,0,2,2])
  for (const contents of [boxed,manual]) {
    const row = rows.filter({hasText:contents})
    await expect(row).toContainText(reference+' · Train carton')
    await expect(row).toContainText('1 unbacked · 1 available to back')
  }
  await rows.getByRole('button',{name:'Transfer stock from '+reference+' · Train carton · '+boxed,exact:true}).click()
  await expect(transfer.getByText(boxed,{exact:true})).toBeVisible()
  await expect(transfer.getByText(manual,{exact:true})).toHaveCount(0)
  await transfer.getByRole('combobox',{name:'Destination bin',exact:true}).selectOption(f.b.id)
  await transfer.getByLabel('Confirm source bin code').fill(bin.code)
  await transfer.getByLabel('Confirm destination bin code').fill(f.b.code)
  await transfer.getByLabel('Count moved').fill('1')
  await transfer.getByLabel('Reason',{exact:true}).fill('Move the boxed train')
  await expect(transfer.locator('.preview')).toContainText(boxed)
  const saved = page.waitForResponse(response => new URL(response.url()).pathname==='/api/v1/receiving/transfers' && response.request().method()==='POST')
  await transfer.getByRole('button',{name:'Confirm transfer'}).click()
  const response = await saved
  expect(response.status()).toBe(200)
  expect(response.request().postDataJSON()).toMatchObject({sourcePlacementId:withBoxId,sourceLocationId:bin.id,destinationLocationId:f.b.id,quantity:1})
  const destinationPositionId = (await response.json()).entityId as string

  await page.goto('/#inventory/lot/'+deliveryId)
  const positions = page.getByRole('region',{name:'Receipt lot positions'}).getByRole('listitem')
  await expect(positions.filter({has:page.locator('a[href="#inventory/'+destinationPositionId+'"]')})).toContainText(boxed)
  await expect(positions.filter({has:page.locator('a[href="#inventory/'+positionId+'"]')})).toContainText(manual)
  await expect(page.getByText('Available to back',{exact:true}).locator('+ dd')).toHaveText('2')
  await page.goto('/'+draftBackingLink(f.b.id,destinationPositionId))
  await expect(page.getByRole('region',{name:'Selected position'})).toContainText(boxed)
  await expect(page.getByText('Available for new backing',{exact:true}).locator('+ dd')).toHaveText('1')
})
