import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture } from './bin-fixture'
import type { RecountBatchPage, RecountPreviewPage } from '../src/api'

const preview = (p:Page)=>p.getByRole('dialog',{name:'Preview recount'})
const go = (p:Page)=>p.getByRole('button',{name:'Preview recount'})
const receipt = (p:Page)=>p.getByRole('region',{name:'Recount receipt',exact:true})
const queue = (p:Page)=>p.getByRole('region',{name:'Recount queue',exact:true})
async function search(page:Page,term:string) {
  const field=page.getByRole('searchbox',{name:'Search stock positions'})
  await field.fill(term)
  await field.press('Enter')
}

test('checked positions survive filters and replay a lost queue receipt, then a witnessed count closes the correct task',async ({page},info)=>{
  const f=await fixture(page)
  const snapshots=await page.request.get('/api/v1/receiving/bins/'+f.a.id+'/counts')
  const line=(await snapshots.json()).positions.find((p:{purchaseId:string})=>p.purchaseId===f.unknown.purchaseId)
  await page.getByRole('link',{name:'Inventory'}).click()
  await search(page,f.a.code)
  for(const id of [f.position,line.positionId]) await page.getByRole('checkbox',{name:'Select position '+id+' for recount'}).check()
  await search(page,f.b.code)
  await expect(page.getByText('2 selected · 2 outside this page')).toBeVisible()
  await expect(go(page)).toBeEnabled()
  await go(page).focus()
  await expect(go(page)).toBeFocused()
  await page.keyboard.press('Enter')
  const dialog=preview(page)
  await expect(dialog.getByRole('listitem')).toHaveCount(2)
  await expect(dialog).toContainText(f.unknown.purchase)
  await expect(dialog).toContainText('Purchase revision 5')
  await dialog.getByRole('combobox',{name:'Action scope'}).selectOption('matching')
  await expect(dialog.getByRole('listitem')).toHaveCount(1)
  await expect(dialog).toContainText(f.b.code)
  await dialog.getByRole('combobox',{name:'Action scope'}).selectOption('selected')
  await expect(dialog.getByRole('listitem')).toHaveCount(2)
  await f.secondOffer()
  await page.setViewportSize({width:320,height:800})
  expect(await dialog.evaluate(el=>el.scrollWidth<=el.clientWidth)).toBe(true)
  await dialog.screenshot({path:info.outputPath('recount-preview-mobile.png')})
  await audit(page)

  const batches='**/api/v1/inventory/recounts/batches'
  let original='',batch=''
  await page.route(batches,async route=>{
    original=route.request().postData()!
    const res=await route.fetch()
    expect(res.status()).toBe(200)
    batch=(await res.json()).entityId
    await res.dispose()
    await route.abort('connectionreset')
  },{times:1})
  await dialog.getByRole('button',{name:'Queue recount'}).click()
  await expect(page.getByRole('button',{name:'Retry command'})).toBeVisible()
  await page.reload()
  await page.route(batches,async route=>{
    expect(route.request().postData()).toBe(original)
    await route.fulfill({status:413,json:{code:'request_too_large',message:'This request exceeds the allowed size.',correlationId:randomUUID()}})
  },{times:1})
  await page.getByRole('button',{name:'Retry command'}).click()
  await page.getByRole('link',{name:'View recount receipt'}).click()
  await expect(receipt(page)).toContainText('2 reviewed · 1 queued · 1 changed · 0 already queued · 0 unavailable')
  const checked=receipt(page).getByRole('listitem')
  await expect(checked.filter({hasText:f.paid.purchase})).toContainText('Changed since preview')
  await expect(checked.filter({hasText:f.unknown.purchase})).toContainText('Queued')
  await page.setViewportSize({width:1440,height:900})
  await page.screenshot({path:info.outputPath('recount-receipt-desktop.png')})

  await receipt(page).getByRole('link',{name:'Recount queue',exact:true}).click()
  const work=queue(page).getByRole('listitem').filter({hasText:line.positionId})
  await expect(queue(page).getByText(/positions awaiting a witnessed count/)).toBeVisible()
  for(let number=2; await work.count()===0; number++) {
    await queue(page).getByRole('button',{name:'Next page'}).click()
    await expect(queue(page).getByText('Page '+number+' ·',{exact:false})).toBeVisible()
  }
  await work.getByRole('link',{name:'Record witnessed count'}).click()
  const count=page.getByRole('dialog',{name:'Count stock',exact:true})
  await count.getByLabel('Confirm physical bin code').fill(f.a.code)
  await count.getByLabel('Witnessed quantity').fill('3')
  await count.getByLabel('Reason for count').fill('Close the requested recount')
  await count.getByRole('button',{name:'Record observation'}).click()
  await expect(page.getByRole('dialog',{name:'Count receipt',exact:true})).toContainText('Close the requested recount')
  const observed=page.url().split('/').at(-1)!
  await page.goto('/#inventory/recounts/'+batch)
  await expect(receipt(page).getByRole('link',{name:'View witnessed count'})).toHaveAttribute('href','#locations/'+f.a.id+'/counts/'+observed)
  await receipt(page).getByRole('link',{name:'Recount queue',exact:true}).click()
  await expect(queue(page).getByRole('listitem').filter({hasText:line.positionId})).toHaveCount(0)
  await queue(page).getByRole('link',{name:'All recount receipts'}).click()
  await page.getByRole('link',{name:'Open recount receipt '+batch}).click()
  await expect(receipt(page)).toContainText(f.unknown.purchase)
})

test('all matches are previewed and queued across register pages; permanently depleted work links to its receipt and timeline',async ({page})=>{
  const f=await fixture(page)
  const name='RECOUNT-'+f.a.code
  const buy=await f.write('receiving/purchases',0,{reference:name,source:'Fictional register',currency:'USD',orderedQuantity:26,pendingChargeMinor:null})
  const drop=await f.write('receiving/deliveries',1,{purchaseId:buy,reference:'Shelves',quantity:26,catalogId:f.catalogId,itemCostMinor:null})
  for(let i=0;i<26;i++) await f.write('receiving/putaway',2+i,{purchaseId:buy,deliveryId:drop,catalogId:f.catalogId,quantity:1,
    locationId:f.a.id,confirmedLocationCode:f.a.code})
  await page.getByRole('link',{name:'Inventory'}).click()
  await search(page,name)
  await expect(page.getByRole('group',{name:'Matching inventory totals'})).toContainText('26')
  const snap=page.waitForResponse(r=>r.request().method()==='POST' && r.url().endsWith('/recounts/previews'))
  await go(page).click()
  const first:RecountPreviewPage=await (await snap).json()
  const dialog=preview(page)
  await expect(dialog).toContainText('26 frozen positions')
  await expect(dialog.getByRole('listitem')).toHaveCount(25)
  await dialog.getByRole('button',{name:'Next targets'}).focus()
  await page.keyboard.press('Enter')
  await expect(dialog.getByRole('listitem')).toHaveCount(1)
  await expect(dialog.getByRole('heading',{name:'26 frozen positions'})).toBeFocused()
  await dialog.getByRole('button',{name:'Queue recount'}).click()
  await expect(receipt(page)).toContainText('26 reviewed · 26 queued')
  await receipt(page).getByRole('button',{name:'Next page'}).click()
  await expect(receipt(page).getByRole('listitem')).toHaveCount(1)

  const before=await (await page.request.get('/api/v1/inventory/recounts/queue?limit=1')).json()
  const position=first.rows[0].positionId
  await f.write('receiving/transfers',28,{purchaseId:buy,sourcePlacementId:position,sourceLocationId:f.a.id,
    confirmedSourceLocationCode:f.a.code,destinationLocationId:f.b.id,confirmedDestinationLocationCode:f.b.code,
    quantity:1,reason:'Return the last piece to the other bin',backing:[]})
  await receipt(page).getByRole('link',{name:'Recount queue',exact:true}).click()
  await expect(queue(page)).toContainText((before.total-1)+' positions awaiting a witnessed count')
  await expect(queue(page).getByRole('listitem').filter({hasText:position})).toHaveCount(0)
  await queue(page).getByRole('link',{name:'All recount receipts'}).click()
  await page.getByRole('link',{name:'Open recount receipt '+first.previewId}).click()
  const row=receipt(page).getByRole('listitem').filter({hasText:position})
  await expect(row).toContainText('Queued')
  await expect(row).toContainText('Depleted before witness')
  await expect(row.getByRole('link',{name:/witnessed count/})).toHaveCount(0)
  await row.getByRole('link',{name:'Inspect position timeline'}).click()
  await expect(page.getByRole('region',{name:'Position timeline'})).toContainText('Return the last piece to the other bin')
  const current:RecountBatchPage=await (await page.request.get('/api/v1/inventory/recounts/batches/'+first.previewId)).json()
  expect(current.rows.find(r=>r.target.positionId===position)?.observationId).toBeNull()
})

test('partial or foreign preview and an offline connection keep the frozen selection pending review', async ({page,context}) => {
  const f=await fixture(page)
  await page.getByRole('link',{name:'Inventory'}).click()
  await search(page,f.a.code)
  await page.getByRole('checkbox',{name:'Select position '+f.position+' for recount'}).check()
  let attempt=0, writes=0
  const ids=new Set<string>()
  page.on('request',req=>{
    if (req.method()==='POST' && req.url().endsWith('/recounts/batches')) writes++
  })
  await page.route('**/api/v1/inventory/recounts/previews', async route=>{
    ids.add(route.request().postDataJSON().previewId)
    const response=await route.fetch()
    const body=await response.json()
    attempt++
    if (attempt===1) body.complete=false
    if (attempt===2) body.previewId=randomUUID()
    await route.fulfill({response,json:body})
    await response.dispose()
  })
  await go(page).click()
  const dialog=preview(page), save=dialog.getByRole('button',{name:'Queue recount'})
  await expect(dialog).toContainText('Partial preview')
  await expect(save).toBeDisabled()
  const retry=dialog.getByRole('button',{name:'Retry preview read'})
  await retry.click()
  await expect(retry).toBeFocused()
  await expect(save).toBeDisabled()
  await expect(dialog.getByRole('listitem')).toHaveCount(0)
  await retry.click()
  await expect(save).toBeEnabled()
  await expect(dialog.getByRole('listitem')).toContainText(f.position)
  await context.setOffline(true)
  await expect(save).toBeDisabled()
  await expect(dialog).toContainText('workspace is disconnected')
  await context.setOffline(false)
  await expect(save).toBeEnabled()
  await dialog.getByRole('button',{name:'Cancel'}).click()
  await expect(go(page)).toBeFocused()
  expect([...ids]).toHaveLength(1)
  expect(writes).toBe(0)
})

test('a delayed recount confirmation preserves a newly opened receipt lot and retains the saved batch link', async ({page}) => {
  const f = await fixture(page)
  await page.getByRole('link', {name:'Inventory'}).click()
  await search(page, f.a.code)
  await page.getByRole('checkbox', {name:'Select position '+f.position+' for recount'}).check()
  await go(page).click()
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  let acknowledge!: (batchId:string) => void
  const committed = new Promise<string>(resolve => { acknowledge = resolve })
  await page.route('**/api/v1/inventory/recounts/batches', async route => {
    const response = await route.fetch()
    acknowledge((await response.json()).entityId)
    await gate
    await route.fulfill({response})
    await response.dispose()
  })
  await preview(page).getByRole('button', {name:'Queue recount'}).click()
  const id = await committed
  const lot = '#inventory/lot/'+f.paid.deliveryId
  await page.evaluate(hash => { window.location.hash = hash }, lot)
  await expect(page.getByRole('region', {name:'Receipt lot positions'})).toBeVisible()
  const refreshed = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/workspace')
  release()
  await (await refreshed).finished()
  await expect(page.getByRole('link', {name:'View recount receipt'})).toHaveAttribute('href', '#inventory/recounts/'+id)
  await expect(page.getByRole('region', {name:'Receipt lot positions'})).toBeVisible()
  expect(new URL(page.url()).hash).toBe(lot)
  await page.goto('/#inventory/recounts/'+id.toUpperCase())
  await expect(receipt(page)).toContainText(f.position)
})
