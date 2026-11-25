import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture, purchases } from './bin-fixture'
import { countPositionLink, lossCaseLink, lossReceiptLink, quarantineLink } from '../src/countLinks'
import type { StockLoss, StockLossCommand } from '../src/api'

const review = (page:Page) => page.getByRole('dialog',{name:'Post stock loss',exact:true})
const proof = (page:Page) => page.getByRole('dialog',{name:'Posted stock loss',exact:true})
const none = (page:Page) => review(page).getByRole('radio',{name:'None of the missing pieces were in quarantine'})
const named = (page:Page) => review(page).getByRole('radio',{name:'Some or all came from named quarantine records'})
const post = (page:Page) => review(page).getByRole('button',{name:'Post stock loss and selected reductions'})
const posting = '**/api/v1/receiving/stock-losses'
const refresh = (page:Page) => page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))

async function counted(page:Page,quantity:number,quarantines:number[],count:number) {
  const f=await fixture(page)
  const purchaseId=await f.write('receiving/purchases',0,{reference:'DAMAGE-'+f.a.code,source:'Fictional stock inspection',currency:'USD',orderedQuantity:quantity,pendingChargeMinor:null})
  const deliveryId=await f.write('receiving/deliveries',1,{purchaseId,reference:'Inspection tray',quantity,catalogId:f.catalogId,itemCostMinor:quantity*100})
  const positionId=await f.write('receiving/putaway',2,{purchaseId,deliveryId,locationId:f.a.id,confirmedLocationCode:f.a.code,catalogId:f.catalogId,quantity})
  const base={purchaseId,positionId,locationId:f.a.id,confirmedLocationCode:f.a.code}
  const holdId=await f.write('receiving/holds',3,{...base,quantity:2,purpose:'Reserve bridge',reason:'Two reserved for demonstration'})
  const offerId=await f.write('receiving/offers',4,{purchaseId,catalogId:f.catalogId,unitPriceMinor:175,backing:[{placementId:positionId,quantity:3}]})
  const records:string[]=[]
  for(const [i,qty] of quarantines.entries()) {
    records.push(await f.write('inventory/quarantines',5+i,{...base,catalogId:f.catalogId,catalogRevision:1,quantity:qty,reason:'Cracked group '+(i+1)}))
  }
  const observation=await f.write('receiving/counts',5+records.length,{...base,countedQuantity:count,reason:'Physical shortage on inspection'})
  return {...f,base,purchaseId,positionId,deliveryId,observation,records,holdId,offerId,revision:6+records.length}
}

async function confirmBin(page:Page,code:string) {
  await review(page).getByLabel('Confirm physical bin code').fill(code)
  await review(page).getByLabel('Reason for posting').fill('Owner checked the damaged and ordinary pieces')
}

test('cross-page attribution consumes the chosen damaged records, protects holds and recovers the exact loss decision',async ({page},info)=>{
  const f=await counted(page,32,Array<number>(26).fill(1),4)
  await page.goto('/'+countPositionLink(f.a.id,f.positionId))
  await page.getByText('Review stock adjustment',{exact:true}).click()
  await page.getByRole('link',{name:'Identify missing pieces and review stock loss'}).press('Enter')
  const d=review(page), summary=d.getByLabel('Loss attribution preview')
  await expect(none(page)).not.toBeChecked()
  await expect(named(page)).not.toBeChecked()
  await expect(post(page)).toBeDisabled()
  await none(page).check()
  await expect(d.getByText(/The selected quantities must fit each record/)).toBeVisible()
  await named(page).focus()
  await page.keyboard.press('Space')
  const fields=d.getByRole('textbox',{name:/^Missing from record/})
  await expect(fields).toHaveCount(25)
  for(const field of await fields.all()) await field.fill('1')
  await d.getByRole('button',{name:'Older quarantine records'}).press('Enter')
  await expect(d.getByRole('heading',{name:'Quarantine records',exact:true})).toBeFocused()
  await expect(fields).toHaveCount(1)
  await fields.fill('1')
  await expect(summary.locator('dd')).toHaveText(['26','2','0','4'])
  await expect(d.getByLabel('Selected quarantine records').getByRole('listitem')).toHaveCount(26)
  await d.getByLabel('Reduce units for offer '+f.offerId,{exact:true}).fill('1')
  await confirmBin(page,f.a.code)
  await page.setViewportSize({width:320,height:760})
  expect(await d.evaluate(el=>el.scrollWidth<=el.clientWidth)).toBe(true)
  await audit(page)
  await post(page).focus()
  await d.screenshot({path:info.outputPath('attributed-loss-confirmation-mobile.png')})
  let original='',postingId=''
  await page.route(posting,async route=>{
    original=route.request().postData()!
    const cmd:StockLossCommand=JSON.parse(original)
    expect(cmd.quarantineAttributions?.map(r=>r.quarantineId).sort()).toEqual([...f.records].sort())
    expect(cmd.offerReductions).toEqual([{offerId:f.offerId,quantity:1}])
    const saved=await route.fetch()
    expect(saved.status()).toBe(200)
    postingId=(await saved.json()).entityId
    await saved.dispose(); await route.abort('connectionreset')
  },{times:1})
  await page.keyboard.press('Enter')
  const retry=page.getByRole('button',{name:'Retry command'})
  await expect(retry).toBeVisible()
  await page.reload()
  await page.route(posting,async route=>{
    expect(route.request().postData()).toBe(original)
    await route.fulfill({status:413,json:{code:'request_too_large',message:'Request size exceeds the limit.',correlationId:randomUUID()}})
  })
  await page.route(url=>url.pathname.endsWith('/posted-loss') && url.searchParams.has('commandId'),async route=>{
    const response=await route.fetch()
    const body:StockLoss=await response.json()
    body.quarantineAttributions![0].quarantineId=randomUUID()
    await route.fulfill({response,json:body}); await response.dispose()
  },{times:1})
  await retry.click()
  await expect(page.getByRole('alert').filter({hasText:'Reconnect to verify'})).toBeVisible()
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await retry.click()
  await page.getByRole('link',{name:'View posted stock loss'}).click()
  await expect(page).toHaveURL(new RegExp(lossReceiptLink(f.a.id,f.positionId,postingId)+'$'))
  const receipt=proof(page), allocation=receipt.getByRole('region',{name:'Posted quarantine attribution'})
  await expect(allocation.locator('dd')).toHaveText(['26','2','26','0'])
  await expect(allocation.getByRole('listitem')).toHaveCount(25)
  await allocation.getByRole('button',{name:'More attributed records'}).press('Enter')
  await expect(allocation.getByRole('heading')).toBeFocused()
  await expect(allocation.getByRole('listitem')).toHaveCount(1)
  const link=allocation.getByRole('link',{name:'Original quarantine receipt'})
  const linked=(await link.getAttribute('href'))!.split('/').at(-1)!
  expect(f.records).toContain(linked)
  await link.click()
  await page.getByRole('link',{name:'Current position quarantine'}).click()
  await expect(page.getByRole('dialog',{name:'Position quarantine'})).toContainText('0 still quarantined · 0 released · 1 lost of 1')
  const [purchase]=await purchases(page,[f.purchaseId])
  expect(purchase.balances).toMatchObject({physicalOnHand:4,unavailable:0,reserved:2,backedQuantity:2})
  expect(purchase.deliveries[0]).toMatchObject({itemCostMinor:3200,allocatedLostItemCostMinor:2800,remainingItemCostMinor:400})
})

for(const source of ['ordinary','quarantine'] as const) test('explicit '+source+' loss from one tray retains the other stock and exact damage evidence',async ({page},info)=>{
  const f=await counted(page,8,[2],6)
  await page.goto('/'+lossCaseLink(f.a.id,f.positionId,f.observation))
  const d=review(page),summary=d.getByLabel('Loss attribution preview')
  if(source==='quarantine') {
    await named(page).check()
    await d.getByLabel('Missing from record '+f.records[0]).fill('2')
    await expect(summary.locator('dd')).toHaveText(['2','0','0','6'])
  } else {
    await named(page).check()
    await d.getByLabel('Missing from record '+f.records[0]).fill('1')
    await none(page).check()
    await expect(summary.locator('dd')).toHaveText(['0','2','2','4'])
    await d.getByLabel('Reduce units for offer '+f.offerId,{exact:true}).fill('1')
  }
  await confirmBin(page,f.a.code)
  if(source==='ordinary') await d.screenshot({path:info.outputPath('ordinary-loss-confirmation-desktop.png')})
  const sent=page.waitForRequest(req=>req.url().endsWith('/receiving/stock-losses') && req.method()==='POST')
  await post(page).click()
  const command=(await sent).postDataJSON() as StockLossCommand
  expect(command.quarantineAttributions).toEqual(source==='ordinary'?[]:[{quarantineId:f.records[0],quantity:2}])
  const receipt=proof(page),section=receipt.getByRole('region',{name:'Posted quarantine attribution'})
  await expect(section.locator('dd')).toHaveText(source==='ordinary'?['0','2','2','2']:['2','0','2','0'])
  await page.reload()
  if(source==='ordinary') await expect(section).toContainText('The operator confirmed that none of the missing pieces were in quarantine.')
  else await expect(section.getByRole('link',{name:'Original quarantine receipt'})).toHaveAttribute('href',quarantineLink(f.a.id,f.positionId,f.records[0]))
  const [purchase]=await purchases(page,[f.purchaseId])
  expect(purchase.balances).toMatchObject({physicalOnHand:6,unavailable:source==='ordinary'?2:0,reserved:2,backedQuantity:source==='ordinary'?2:3})
})

test('incomplete or foreign damage evidence and a renewed witnessed count require review before posting',async ({page,context})=>{
  const f=await counted(page,8,[1,1],4)
  const recordScope=(url:URL)=>url.pathname.endsWith('/positions/'+f.positionId+'/quarantine')
  await page.route(recordScope,async route=>{
    const response=await route.fetch()
    await route.fulfill({response,json:{...await response.json(),complete:false}})
    await response.dispose()
  },{times:1})
  await page.goto('/'+lossCaseLink(f.a.id,f.positionId,f.observation))
  const d=review(page),again=d.getByRole('button',{name:'Retry loss review'})
  await expect(d).toContainText('Incomplete quarantine records')
  await expect(named(page)).toHaveCount(0)
  await page.route(recordScope,async route=>{
    const response=await route.fetch()
    const body=await response.json()
    body.position.layer.placementId=randomUUID()
    await route.fulfill({response,json:body})
    await response.dispose()
  },{times:1})
  await again.click()
  await expect(d).toContainText('could not verify the server response')
  await expect(named(page)).toHaveCount(0)
  await again.click()
  await named(page).check()
  await d.getByLabel('Missing from record '+f.records[0]).fill('1')
  await d.getByLabel('Reduce units for offer '+f.offerId).fill('2')
  await confirmBin(page,f.a.code)
  await expect(post(page)).toBeEnabled()
  await context.setOffline(true)
  await expect(post(page)).toBeDisabled()
  await context.setOffline(false)
  await expect(post(page)).toBeEnabled()
  await f.write('receiving/counts',f.revision,{...f.base,countedQuantity:2,reason:'Two pieces on repeat inspection'})
  await refresh(page)
  await expect(d).toContainText('The count or purchase changed.')
  await expect(post(page)).toBeDisabled()
  await d.getByRole('button',{name:'Review updated quantities'}).click()
  await expect(named(page)).not.toBeChecked()
  await none(page).check()
  await expect(d).toContainText('Build holds exceed remaining stock by 2.')
  await expect(d.getByRole('link',{name:'Inspect named build holds'})).toBeVisible()
  await expect(post(page)).toBeDisabled()
  const [p]=await purchases(page,[f.purchaseId])
  expect(p.balances).toMatchObject({physicalOnHand:8,unavailable:2,reserved:2,backedQuantity:3})
})
