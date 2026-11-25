import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture, purchases, signIn } from './bin-fixture'
import { draftBackingLink } from '../src/countLinks'
import type { DraftBackingPage, DraftOfferHistory } from '../src/api'

const view = (p:Page) => p.getByRole('dialog',{name:'Draft offer backing',exact:true})
const proof = (p:Page) => p.getByRole('dialog',{name:'Withdrawal receipt',exact:true})
const posting = '**/api/v1/inventory/draft-backing-withdrawals'
const path = (position:string) => '/api/v1/inventory/positions/'+position+'/draft-backing'
const review = (page:Page,id:string,bin:string,offer:string) => page.goto('/'+draftBackingLink(bin,id,'offers',offer))
async function fill(page:Page,bin:string,quantity:number,reason:string) {
  const modal=view(page)
  await modal.getByLabel('Quantity to withdraw').fill(String(quantity))
  await modal.getByLabel('Confirm physical bin code').fill(bin)
  await modal.getByLabel('Reason for withdrawal').fill(reason)
}
async function eight(page:Page) {
  const f=await fixture(page), reference='EIGHT-'+f.a.code
  const purchaseId=await f.write('receiving/purchases',0,{reference,source:'Fictional purchase',currency:'USD',orderedQuantity:8,pendingChargeMinor:null})
  const deliveryId=await f.write('receiving/deliveries',1,{purchaseId,quantity:8,reference:'Eight pieces',catalogId:f.catalogId,itemCostMinor:800})
  const position=await f.write('receiving/putaway',2,{purchaseId,deliveryId,locationId:f.a.id,catalogId:f.catalogId,confirmedLocationCode:f.a.code,quantity:8})
  await f.write('receiving/holds',3,{purchaseId,positionId:position,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:2,purpose:'Bridge test',reason:'Reserve two for a bridge'})
  const offer=await f.write('receiving/offers',4,{purchaseId,catalogId:f.catalogId,unitPriceMinor:175,backing:[{placementId:position,quantity:3}]})
  return {...f,purchaseId,reference,deliveryId,position,offer}
}

test('selected draft backing can be partially and fully withdrawn without losing its price, stock, cost or history',async ({page},info)=>{
  const f=await eight(page)
  await page.goto('/#receiving/'+f.purchaseId)
  const chosen=page.getByRole('list',{name:'Unpublished offers'}).getByRole('listitem').filter({hasText:f.offer})
  await chosen.getByRole('link',{name:'Review draft offer '+f.offer+' at position '+f.position}).click()
  const dialog=view(page), published=proof(page)
  await expect(dialog.getByLabel('Position balances').locator('dd')).toHaveText(['8','2','6','3','3','3'])
  const start=dialog.getByRole('button',{name:'Withdraw backing',exact:true})
  await start.focus(); await page.keyboard.press('Enter')
  await expect(dialog.getByRole('heading',{name:'Withdraw draft backing'})).toBeFocused()
  await fill(page,f.b.code,1,'Reserve one piece for another draft')
  const save=dialog.getByRole('button',{name:'Confirm withdrawal'})
  await expect(save).toBeDisabled()
  await dialog.getByLabel('Confirm physical bin code').fill(f.a.code)
  await dialog.getByLabel('Quantity to withdraw').fill('4'); await expect(save).toBeDisabled()
  await dialog.getByLabel('Quantity to withdraw').fill('1')
  await expect(dialog.getByLabel('Proposed withdrawal').locator('dd')).toHaveText(['3 → 2','3 → 2','3 → 4'])
  await page.screenshot({path:info.outputPath('draft-withdrawal-desktop.png')})
  await page.setViewportSize({width:320,height:760})
  await expect(save).toBeEnabled()
  expect(await dialog.evaluate(e=>e.scrollWidth<=e.clientWidth)).toBe(true)
  await save.focus()
  await expect(save).toBeFocused()
  await dialog.screenshot({path:info.outputPath('draft-withdrawal-mobile.png')})
  await audit(page)
  await save.click()
  await expect(published).toContainText('1 unit withdrawn')
  await expect(published).toContainText('Reserve one piece for another draft')
  const id=page.url().split('/').at(-1)!
  await page.reload()
  await expect(published).toContainText(f.offer)
  await published.getByRole('link',{name:'Review this offer at this position'}).click()
  await expect(dialog.getByLabel('Position balances').locator('dd')).toHaveText(['8','2','6','2','4','4'])
  await start.click(); await fill(page,f.a.code,2,'Withdraw the remaining draft backing'); await save.click()
  await expect(published).toContainText('2 units withdrawn')
  const final=page.url().split('/').at(-1)!
  await published.getByRole('link',{name:'View purchase'}).click()
  await expect(chosen).toContainText('1.75 per unit')
  await expect(chosen).toContainText('0 backed')
  const [purchase]=await purchases(page,[f.purchaseId])
  expect(purchase.balances).toMatchObject({physicalOnHand:8,free:6,reserved:2,withheldQuantity:6,knownItemCostMinor:800,publishedQuantity:0})
  expect(purchase.offers.find(o=>o.id===f.offer)).toMatchObject({backedQuantity:0,unitPriceMinor:175,backing:[]})
  await review(page,f.position,f.a.id,f.offer)
  await expect(start).toBeDisabled()
  const history=dialog.getByRole('list',{name:'Offer backing history'})
  await expect(history.getByRole('link',{name:'Withdrawal receipt'})).toHaveCount(2)
  await expect(history).toContainText('Offer backing added')
  await dialog.getByRole('link',{name:'Position timeline'}).click()
  const events=page.getByRole('region',{name:'Position timeline'})
  await expect(events.getByRole('listitem').filter({hasText:'Reserve one piece for another draft'}).getByRole('link',{name:'View withdrawal receipt'})).toHaveAttribute('href',draftBackingLink(f.a.id,f.position,'receipts',id))
  await page.goto('/#inventory/lot/'+f.deliveryId)
  const lot=page.getByRole('region',{name:'Receipt lot activity'})
  await lot.getByRole('listitem').filter({hasText:'Withdraw the remaining draft backing'}).getByRole('link',{name:'View withdrawal receipt'}).click()
  await expect(published).toContainText(final)
})

test('position offer pages preserve exact selection even with a sibling allocation and a concurrent change',async ({page})=>{
  const f=await fixture(page)
  const purchaseId=await f.write('receiving/purchases',0,{reference:'PAGES-'+f.a.code,source:'Fictional purchase',currency:'EUR',orderedQuantity:30,pendingChargeMinor:null})
  const deliveryId=await f.write('receiving/deliveries',1,{purchaseId,catalogId:f.catalogId,reference:'Many internal drafts',quantity:30,itemCostMinor:null})
  const allocate=(revision:number,bin:typeof f.a,quantity:number)=>f.write('receiving/putaway',revision,{purchaseId,deliveryId,catalogId:f.catalogId,locationId:bin.id,confirmedLocationCode:bin.code,quantity})
  const pos=await allocate(2,f.a,28), sibling=await allocate(3,f.b,2)
  for(let i=0;i<26;i++) await f.write('receiving/offers',4+i,{purchaseId,catalogId:f.catalogId,unitPriceMinor:150,backing:[{placementId:pos,quantity:1}]})
  const shared=await f.write('receiving/offers',30,{purchaseId,catalogId:f.catalogId,unitPriceMinor:950,backing:[{placementId:pos,quantity:2},{placementId:sibling,quantity:2}]})
  await page.goto('/'+draftBackingLink(f.a.id,pos))
  const d=view(page), rows=d.getByRole('list',{name:'Offers at this position'}).getByRole('listitem')
  await expect(rows).toHaveCount(25)
  await d.getByRole('button',{name:'Next page'}).focus(); await page.keyboard.press('Enter')
  await expect(rows).toHaveCount(2)
  await expect(d.getByRole('heading',{name:'Draft offer backing'})).toBeFocused()
  const link=rows.first().getByRole('link')
  const target=(await link.getAttribute('href'))!.split('/').at(-1)!
  await link.click()
  await expect(d.getByRole('region',{name:'Selected offer'})).toContainText(target)
  await review(page,pos,f.a.id,shared)
  await d.getByRole('button',{name:'Withdraw backing',exact:true}).click()
  await fill(page,f.a.code,1,'Release a shared allocation')
  await expect(d.getByLabel('Proposed withdrawal').locator('dd')).toHaveText(['2 → 1','4 → 3','0 → 1'])
  await f.write('inventory/draft-backing-withdrawals',31,{purchaseId,positionId:pos,offerId:shared,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:1,reason:'Second operator reviewed the same position'})
  await d.getByRole('button',{name:'Confirm withdrawal'}).click()
  await expect(d).toContainText('The position or offer changed')
  await expect(d.getByRole('button',{name:'Confirm withdrawal'})).toBeDisabled()
  await d.getByRole('button',{name:'Cancel'}).click()
  await expect(d.getByRole('region',{name:'Selected offer'})).toContainText('1 backed at this position · 3 across all positions')
  await d.getByRole('button',{name:'Withdraw backing',exact:true}).click()
  await fill(page,f.a.code,1,'Final source allocation')
  await d.getByRole('button',{name:'Confirm withdrawal'}).click()
  await expect(proof(page)).toContainText('Final source allocation')
  const response=await page.request.get(path(sibling)+'/'+shared+'/history')
  const other:DraftOfferHistory=await response.json()
  expect(other.offer).toMatchObject({backedQuantity:2,totalOfferBackedQuantity:2,currency:'EUR',unitPriceMinor:950})
  await f.write('receiving/counts',33,{purchaseId,positionId:sibling,locationId:f.b.id,confirmedLocationCode:f.b.code,countedQuantity:1,reason:'One absent from sibling'})
  await review(page,sibling,f.b.id,shared)
  await expect(d).toContainText('This position has an open shortage')
  await expect(d.getByRole('button',{name:'Withdraw backing',exact:true})).toBeDisabled()
})

test('partial, foreign, offline and lost confirmation preserve the original withdrawal command and exact receipt',async ({page,context})=>{
  const f=await eight(page), endpoint=path(f.position)+'/'+f.offer+'/history'
  const scope=(u:URL)=>u.pathname===endpoint
  await page.route(scope,async route=>{
    const response=await route.fetch()
    await route.fulfill({response,json:{...await response.json(),complete:false}})
    await response.dispose()
  },{times:1})
  await review(page,f.position,f.a.id,f.offer)
  const d=view(page)
  await expect(d).toContainText('Partial backing read')
  await expect(d.getByRole('button',{name:'Withdraw backing',exact:true})).toHaveCount(0)
  await d.getByRole('button',{name:'Retry backing read'}).click()
  await d.getByRole('button',{name:'Withdraw backing',exact:true}).click()
  await fill(page,f.a.code,1,'Journal qualification')
  const save=d.getByRole('button',{name:'Confirm withdrawal'})
  await context.setOffline(true)
  await expect(save).toBeDisabled()
  await context.setOffline(false)
  await expect(save).toBeEnabled()
  await page.route(scope,async route=>{
    const response=await route.fetch(), current=await response.json()
    await route.fulfill({response,json:{...current,offer:{...current.offer,offerId:randomUUID()}}})
    await response.dispose()
  },{times:1})
  await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))
  await expect(d).toContainText('could not verify the server response')
  await expect(save).toBeDisabled()
  await d.getByRole('button',{name:'Retry backing read'}).click()
  await expect(save).toBeEnabled()
  let original='', saved=''
  await page.route(posting,async route=>{
    original=route.request().postData()!
    const response=await route.fetch()
    expect(response.ok()).toBe(true)
    saved=(await response.json()).entityId; await response.dispose()
    await route.abort('connectionreset')
  },{times:1})
  await save.click()
  const retry=page.getByRole('button',{name:'Retry command'})
  await expect(retry).toBeVisible()
  await page.reload()
  await page.route(posting,async route=>{
    expect(route.request().postData()).toBe(original)
    await route.fulfill({status:413,json:{code:'request_too_large',message:'Request size exceeds the limit.',correlationId:randomUUID()}})
  })
  const exact=(u:URL)=>u.pathname===path(f.position)+'/receipts' && u.searchParams.get('commandId')===JSON.parse(original).commandId
  for(const change of [{offerId:randomUUID()},{complete:false}]) {
    await page.route(exact,async route=>{
      const response=await route.fetch()
      await route.fulfill({response,json:{...await response.json(),...change}})
      await response.dispose()
    },{times:1})
    await retry.click()
    await expect(page.getByRole('alert').filter({hasText:'Reconnect to verify'})).toBeVisible()
    await page.getByRole('button',{name:'Refresh',exact:true}).click()
    await expect(retry).toBeEnabled()
  }
  await retry.click()
  await page.getByRole('link',{name:'View withdrawal receipt'}).click()
  await expect(proof(page)).toContainText(saved)
  const result:DraftBackingPage=await (await page.request.get(path(f.position))).json()
  expect(result.offers.find(o=>o.offerId===f.offer)?.backedQuantity).toBe(2)
})

test('paged offer history restarts after a competing revision and two operators can withdraw the last unit only once',async ({page,browser,baseURL})=>{
  const f=await fixture(page)
  const purchaseId=await f.write('receiving/purchases',0,{reference:'HISTORY-'+f.a.code,source:'Fictional purchase',currency:'USD',orderedQuantity:28,pendingChargeMinor:null})
  const delivery=await f.write('receiving/deliveries',1,{purchaseId,catalogId:f.catalogId,reference:'Twenty-eight',quantity:28,itemCostMinor:null})
  const pos=await f.write('receiving/putaway',2,{purchaseId,deliveryId:delivery,catalogId:f.catalogId,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:28})
  const offer=await f.write('receiving/offers',3,{purchaseId,catalogId:f.catalogId,unitPriceMinor:225,backing:[{placementId:pos,quantity:28}]})
  const base={purchaseId,offerId:offer,positionId:pos,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:1}
  for(let i=0;i<26;i++) await f.write('inventory/draft-backing-withdrawals',4+i,{...base,reason:'Single-piece release '+i})
  await review(page,pos,f.a.id,offer)
  const d=view(page), list=d.getByRole('list',{name:'Offer backing history'})
  await expect(list.getByRole('listitem')).toHaveCount(25)
  await d.getByRole('button',{name:'Next page'}).click()
  await expect(list.getByRole('listitem')).toHaveCount(2)
  await expect(list).toContainText('Offer backing added')
  await f.write('inventory/draft-backing-withdrawals',30,{...base,reason:'Leave one backed piece'})
  await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))
  await d.getByRole('button',{name:'Read from first page'}).click()
  await expect(list.getByRole('listitem')).toHaveCount(25)
  await expect(list).toContainText('Leave one backed piece')
  const other=await browser.newContext({baseURL})
  try {
    const second=await other.newPage()
    await review(second,pos,f.a.id,offer)
    await signIn(second)
    let arrivals=0, release!:()=>void
    const together=new Promise<void>(resolve=>{release=resolve})
    for(const [i,p] of [page,second].entries()) {
      await view(p).getByRole('button',{name:'Withdraw backing',exact:true}).click()
      await fill(p,f.a.code,1,'Request for the last unit '+i)
      await p.route(posting,async route=>{
        if(++arrivals===2) release()
        await together
        await route.continue()
      },{times:1})
    }
    const responses=Promise.all([page,second].map(p=>p.waitForResponse(r=>r.request().method()==='POST' && r.url().endsWith('/draft-backing-withdrawals'))))
    await Promise.all([page,second].map(p=>view(p).getByRole('button',{name:'Confirm withdrawal'}).click()))
    const answers=await responses
    expect(answers.map(r=>r.status()).sort()).toEqual([200,409])
    const winner=answers[0].ok()?page:second, loser=winner===page?second:page
    await expect(proof(winner)).toContainText('1 unit withdrawn')
    await expect(view(loser)).toContainText('The position or offer changed')
    const current:DraftOfferHistory=await (await page.request.get(path(pos)+'/'+offer+'/history')).json()
    expect(current.offer).toMatchObject({backedQuantity:0,totalOfferBackedQuantity:0,unitPriceMinor:225})
    expect(current.contributions.filter(row=>row.reason?.startsWith('Request for the last unit'))).toHaveLength(1)
  } finally {await other.close()}
})
