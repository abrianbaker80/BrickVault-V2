import { readFileSync } from 'node:fs'
import { request } from 'node:https'
import { expect, type Page } from '@playwright/test'
import { test } from './trusted'
import { audit, fixture } from './bin-fixture'
import { countReceiptLink, lossCaseLink, lossReceiptLink } from '../src/countLinks'
import type { CountSnapshot, ReceivingWorkspace, StockLoss, StockLossCommand } from '../src/api'

const form = (page:Page) => page.getByRole('dialog',{name:'Post stock loss',exact:true})
const receipt = (page:Page) => page.getByRole('dialog',{name:'Posted stock loss',exact:true})
const commit = (page:Page) => form(page).getByRole('button',{name:'Post stock loss and selected reductions'})
const postPath = '**/api/v1/receiving/stock-losses'
const refresh = (page:Page) => page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))
const reductions = (page:Page) => form(page).getByRole('region',{name:'Selected offer reductions'})
async function describe(page:Page,code:string,reason:string) {
  await form(page).getByLabel('Confirm physical bin code').fill(code)
  await form(page).getByLabel('Reason for posting').fill(reason)
}

test('one cross-page loss decision reduces only the chosen offers at the selected position and survives a lost response', async ({page}, info) => {
  const f = await fixture(page)
  const purchaseId = await f.write('receiving/purchases',0,{reference:'LOSS-' + f.a.code,source:'Fictional count test',currency:'USD',orderedQuantity:31,pendingChargeMinor:null})
  const deliveryId = await f.write('receiving/deliveries',1,{purchaseId,reference:'Tray of 31',quantity:31,catalogId:f.catalogId,itemCostMinor:3100})
  const placed = (rev:number,bin:typeof f.a,quantity:number) => f.write('receiving/putaway',rev,{purchaseId,deliveryId,catalogId:f.catalogId,locationId:bin.id,confirmedLocationCode:bin.code,quantity})
  const positionId = await placed(2,f.a,29)
  const sibling = await placed(3,f.b,2)
  let shared = ''
  for(let n=0;n<27;n++) {
    const backing = [{placementId:positionId,quantity:1}]
    if(!n) backing.push({placementId:sibling,quantity:1})
    const id = await f.write('receiving/offers',4+n,{purchaseId,catalogId:f.catalogId,unitPriceMinor:100+n,backing})
    if(!n) shared=id
  }
  const base = {purchaseId,positionId,locationId:f.a.id,confirmedLocationCode:f.a.code}
  await f.write('receiving/holds',31,{...base,quantity:1,purpose:'Bridge display',reason:'Keep the named build reserved'})
  const opening = await f.write('receiving/counts',32,{...base,countedQuantity:1,reason:'Initial observation'})
  const latest = await f.write('receiving/counts',33,{...base,countedQuantity:2,reason:'Witnessed final count'})
  await page.goto('/'+countReceiptLink(f.a.id,opening))
  await page.getByText('Review stock adjustment',{exact:true}).click()
  const firstPage = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/adjustment-review'))
  await page.getByRole('link',{name:'Choose reductions and post stock loss'}).click()
  const work = form(page)
  await expect(work).toContainText('Reduce exactly 26 units')
  await expect(work.getByRole('link',{name:'Latest witnessed count'})).toHaveAttribute('href',countReceiptLink(f.a.id,latest))
  const offerRows = work.getByRole('list',{name:'Offer backing on this position'})
  await expect(offerRows.getByRole('listitem')).toHaveCount(25)
  const ids: string[] = (await (await firstPage).json()).offers.map((o:{id:string}) => o.id)
  const first = ids.includes(shared) ? shared : ids[0]
  const input = (id:string) => work.getByLabel('Reduce units for offer ' + id,{exact:true})
  await expect(input(first)).toHaveValue('')
  await expect(commit(page)).toBeDisabled()
  const kept = ids.find(id => id !== first)!
  await input(kept).fill('1')
  await input(kept).fill('0')
  await expect(reductions(page)).not.toContainText(kept)
  const chosen = ids.filter(id => id !== kept)
  for (const id of chosen) await input(id).fill('1')
  const next = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/adjustment-review') && new URL(r.url()).searchParams.has('after'))
  await work.getByRole('button',{name:'More offers'}).press('Enter')
  await expect(offerRows.getByRole('listitem')).toHaveCount(2)
  const lastPage: string[] = (await (await next).json()).offers.map((o:{id:string}) => o.id)
  for (const id of lastPage) await input(id).fill('1')
  chosen.push(...lastPage)
  expect(chosen).toContain(shared)
  await work.getByRole('button',{name:'Previous offers'}).press('Enter')
  await expect(input(first)).toHaveValue('1')
  await expect(reductions(page)).toContainText(lastPage[1])
  await page.setViewportSize({width:320,height:720})
  await describe(page,f.b.code,'Owner selected the exact backing to reduce')
  await expect(commit(page)).toBeDisabled()
  await work.getByLabel('Confirm physical bin code').fill(f.a.code)
  await expect(commit(page)).toBeEnabled()
  await commit(page).scrollIntoViewIfNeeded()
  expect(await work.evaluate(el=>el.scrollWidth<=el.clientWidth)).toBe(true)
  await page.screenshot({path:info.outputPath('stock-loss-selection-mobile.png')})
  await audit(page)
  let original = '', postingId = ''
  await page.route(postPath,async route=>{
    original=route.request().postData()!
    const data:StockLossCommand=JSON.parse(original)
    expect(data.offerReductions.map(r=>r.offerId).sort()).toEqual([...chosen].sort())
    expect(data.expectedRevision).toBe(34)
    expect(data.expectedObservationId).toBe(latest)
    const ack=await route.fetch()
    expect(ack.ok()).toBe(true)
    postingId=(await ack.json()).entityId
    await ack.dispose(); await route.abort('connectionreset')
  },{times:1})
  await commit(page).press('Enter')
  const pending=page.getByRole('button',{name:'Retry command'})
  await expect(pending).toBeVisible()
  await page.reload()
  const retry=page.waitForRequest(req=>req.method()==='POST' && req.url().endsWith('/receiving/stock-losses'))
  await pending.click()
  expect((await retry).postData()).toBe(original)
  await page.getByRole('link',{name:'View posted stock loss'}).click()
  await expect(page).toHaveURL(new RegExp(lossReceiptLink(f.a.id,positionId,postingId)+'$'))
  await expect(receipt(page)).toContainText('27 units of stock loss posted')
  await expect(receipt(page).getByRole('listitem')).toHaveCount(25)
  await receipt(page).getByRole('button',{name:'More reductions'}).press('Enter')
  await expect(receipt(page).getByRole('listitem')).toHaveCount(1)
  await expect(receipt(page).getByRole('heading',{name:'Posted offer reductions'})).toBeFocused()
  await expect(receipt(page).getByRole('link',{name:'Witnessed count used for this posting'})).toHaveAttribute('href',countReceiptLink(f.a.id,latest))
  await page.reload()
  await expect(receipt(page)).toContainText('Owner selected the exact backing to reduce')
  await page.setViewportSize({width:1440,height:900})
  await page.screenshot({path:info.outputPath('posted-stock-loss-desktop.png')})
  await audit(page)
  const response=await page.request.get('/api/v1/receiving/current?holds=true&losses=true&quarantine=true')
  const purchase=((await response.json()) as ReceivingWorkspace).purchases.find(p=>p.id===purchaseId)!
  expect(purchase.balances.physicalOnHand).toBe(4)
  expect(purchase.balances.reserved).toBe(1)
  expect(purchase.deliveries[0]).toMatchObject({quantity:31,intakeQuantity:0,lostQuantity:27,allocatedLostItemCostMinor:2700,remainingItemCostMinor:400})
  expect(purchase.offers.find(o=>o.id===shared)?.backing).toEqual([{placementId:sibling,quantity:1}])
  expect(purchase.offers.find(o=>o.id===kept)?.backedQuantity).toBe(1)
  expect(purchase.offers.filter(o=>o.id!==shared && o.id!==kept).every(o=>o.backedQuantity===0)).toBe(true)
  await page.goto('/'+countReceiptLink(f.a.id,opening))
  await expect(page.getByRole('dialog')).toContainText('A later witnessed count was used')
  await expect(page.getByRole('dialog').getByRole('link',{name:'View posted stock loss'})).toHaveAttribute('href',lossReceiptLink(f.a.id,positionId,postingId))
  await page.goto('/'+countReceiptLink(f.a.id,latest))
  await expect(page.getByRole('dialog')).toContainText('This observation was used to post a stock loss.')
})

test('a revised count invalidates the decision and 413 recovery verifies the stored posting for a depleted unknown-cost position', async ({page,context})=>{
  const f=await fixture(page)
  const positions=await page.request.get('/api/v1/receiving/bins/'+f.a.id+'/counts')
  const p=((await positions.json()) as CountSnapshot).positions.find(p=>p.purchaseId===f.unknown.purchaseId)!
  const base={purchaseId:p.purchaseId,positionId:p.positionId,locationId:f.a.id,confirmedLocationCode:f.a.code}
  const first=await f.write('receiving/counts',p.purchaseRevision,{...base,countedQuantity:1,reason:'Initial physical count'})
  await page.goto('/'+lossCaseLink(f.a.id,p.positionId,first))
  await expect(form(page)).toContainText('No unpublished offers use this position.')
  await describe(page,f.a.code,'Prior decision')
  await expect(commit(page)).toBeEnabled()
  await context.setOffline(true)
  await expect(commit(page)).toBeDisabled()
  await context.setOffline(false)
  await expect(commit(page)).toBeEnabled()
  const latest=await f.write('receiving/counts',p.purchaseRevision+1,{...base,countedQuantity:0,reason:'Witnessed empty position'})
  await refresh(page)
  await expect(form(page)).toContainText('The count or purchase changed.')
  await expect(commit(page)).toBeDisabled()
  await form(page).getByRole('button',{name:'Review updated quantities'}).click()
  await expect(form(page).getByLabel('Reason for posting')).toHaveValue('')
  await describe(page,f.a.code,'No pieces remain after recount')
  let original='',posting=''
  await page.route(postPath,async route=>{
    original=route.request().postData()!
    const data:StockLossCommand=JSON.parse(original)
    expect(data.offerReductions).toEqual([])
    expect(data.expectedObservationId).toBe(latest)
    const ack=await route.fetch()
    expect(ack.ok()).toBe(true)
    posting=(await ack.json()).entityId
    await ack.dispose(); await route.abort('connectionreset')
  },{times:1})
  await commit(page).click()
  const retry=page.getByRole('button',{name:'Retry command'})
  await expect(retry).toBeVisible()
  await page.reload()
  const exact=(u:URL)=>u.pathname.endsWith('/positions/'+p.positionId+'/posted-loss') && u.searchParams.get('commandId')===JSON.parse(original).commandId
  await page.route(postPath,async route=>{
    expect(route.request().postData()).toBe(original)
    const headers=await route.request().allHeaders()
    const body = await new Promise<string>((resolve,reject)=>{
      const total=128*1024*1024+1
      const req=request(route.request().url(),{method:'POST',ca:readFileSync(process.env.BV_LAB_CA!),headers:{
        ...headers,'content-length':String(total),'accept-encoding':'identity',connection:'close'
      }},res=>{
        if(res.statusCode!==413) {reject(new Error('Unexpected stock-loss size response '+res.statusCode)); req.destroy(); return}
        const parts:Buffer[]=[]
        res.on('data',part=>parts.push(part))
        res.on('end',()=>{resolve(Buffer.concat(parts).toString()); req.destroy()})
        res.on('error',reject)
      })
      req.on('error',reject)
      req.setTimeout(15000,()=>req.destroy(new Error('Stock-loss size probe timed out')))
      // The authenticated endpoint checks the declared size before reading the body.
      req.flushHeaders()
      req.write(Buffer.alloc(4096,32))
    })
    expect(JSON.parse(body).code).toBe('request_too_large')
    await route.fulfill({status:413,contentType:'application/json',body})
  },{times:2})
  await page.route(exact,async route=>{
    const response=await route.fetch()
    const data:StockLoss=await response.json()
    await route.fulfill({json:{...data,reason:'Different immutable decision'}}); await response.dispose()
  },{times:1})
  await retry.click()
  await expect(page.getByRole('alert')).toContainText('verify the original command')
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await retry.click()
  await expect(retry).toHaveCount(0)
  await page.getByRole('link',{name:'View posted stock loss'}).click()
  await expect(receipt(page)).toContainText('No offer backing was reduced.')
  await expect(receipt(page).locator('dd')).toHaveText(['3','0','0','0','0','0','Unknown','Unknown','Unknown'])
  await page.goto('/'+lossCaseLink(f.a.id,p.positionId,first))
  await expect(form(page).getByRole('link',{name:'View posted stock loss'})).toHaveAttribute('href',lossReceiptLink(f.a.id,p.positionId,posting))
})

test('a protected hold blocks posting and a newly arrived witness rejects a previously reviewed decision', async ({page}) => {
  const f=await fixture(page)
  const base={purchaseId:f.paid.purchaseId,positionId:f.position,locationId:f.a.id,confirmedLocationCode:f.a.code}
  const holdId=await f.write('receiving/holds',5,{...base,purpose:'Protected display',quantity:2,reason:'Protected for its owner'})
  const first=await f.write('receiving/counts',6,{...base,countedQuantity:1,reason:'One piece witnessed'})
  await page.goto('/'+lossCaseLink(f.a.id,f.position,first))
  const work=form(page)
  await expect(work).toContainText('Build holds exceed remaining stock by 1.')
  await expect(work.getByRole('link',{name:'Inspect named build holds'})).toHaveAttribute('href',new RegExp(f.position+'/holds$'))
  await expect(commit(page)).toBeDisabled()
  await expect(work.getByLabel('Confirm physical bin code')).toBeDisabled()
  await f.write('receiving/holds/release',7,{purchaseId:base.purchaseId,holdId,quantity:1,shortageId:first,expectedObservationId:first,
    reason:'Owner selected one display unit'})
  await refresh(page)
  await expect(work).toContainText('The count or purchase changed.')
  await work.getByRole('button',{name:'Review updated quantities'}).click()
  await work.getByLabel('Reduce units for offer '+f.firstOffer,{exact:true}).fill('3')
  await describe(page,f.a.code,'Decision based on one observed piece')
  let newer=''
  let rejected=''
  await page.route(postPath,async route=>{
    newer=await f.write('receiving/counts',8,{...base,countedQuantity:2,reason:'Two pieces on the latest review'})
    const ack=await route.fetch()
    expect(ack.status()).toBe(409)
    rejected=(await ack.json()).code
    await route.fulfill({response:ack}); await ack.dispose()
  },{times:1})
  await commit(page).click()
  await expect(work).toContainText('The count or purchase changed.')
  expect(['revision_conflict','stock_loss_observation_conflict']).toContain(rejected)
  await expect(commit(page)).toBeDisabled()
  expect(await page.evaluate(()=>Object.keys(localStorage).filter(k=>k.startsWith('brickvault.v2.pending.')))).toEqual([])
  await work.getByRole('button',{name:'Review updated quantities'}).click()
  await expect(work.getByRole('link',{name:'Latest witnessed count'})).toHaveAttribute('href',countReceiptLink(f.a.id,newer))
  await expect(work.getByLabel('Reduce units for offer '+f.firstOffer,{exact:true})).toHaveValue('')
  const response=await page.request.get('/api/v1/receiving/current?holds=true&losses=true&quarantine=true')
  const current=((await response.json()) as ReceivingWorkspace).purchases.find(p=>p.id===base.purchaseId)!
  expect(current.balances).toMatchObject({physicalOnHand:8,reserved:1,backedQuantity:3})
  expect(current.deliveries[0].lostQuantity).toBe(0)
})

test('same-document scope changes clear the previous decision and posted receipt before the new read returns', async ({page}) => {
  const f=await fixture(page)
  const scoped=await page.request.get('/api/v1/receiving/bins/'+f.a.id+'/counts')
  const other=((await scoped.json()) as CountSnapshot).positions.find(p=>p.purchaseId===f.unknown.purchaseId)!
  const base={purchaseId:f.paid.purchaseId,positionId:f.position,locationId:f.a.id,confirmedLocationCode:f.a.code}
  const witness=await f.write('receiving/counts',5,{...base,countedQuantity:1,reason:'Original bin witness'})
  const caseRoute=lossCaseLink(f.a.id,f.position,witness)
  const navigate=(hash:string)=>page.evaluate(next=>{window.location.hash=next},hash)
  const input=form(page).getByLabel('Reduce units for offer '+f.firstOffer,{exact:true})
  const decision='Only for the original position'
  await page.goto('/'+caseRoute)
  let posting=''
  for(const [bin,position] of [[f.a.id,other.positionId],[f.b.id,f.position]]) {
    await input.fill('2')
    await describe(page,f.a.code,decision)
    await expect(commit(page)).toBeEnabled()
    const path='/api/v1/receiving/bins/'+bin+'/positions/'+position+'/shortages/'+witness+'/adjustment-review'
    let resume!:()=>void
    const gate=new Promise<void>(resolve=>{resume=resolve})
    await page.route(u=>u.pathname===path,async route=>{await gate;await route.continue()},{times:1})
    const reading=page.waitForRequest(r=>new URL(r.url()).pathname===path)
    await navigate(lossCaseLink(bin,position,witness))
    await reading
    try {
      await expect(form(page)).toContainText('Reading stock and offer backing')
      await expect(input).toHaveCount(0)
      await expect(form(page).getByLabel('Reason for posting')).toHaveCount(0)
      await expect(commit(page)).toHaveCount(0)
    } finally {resume()}
    await expect(form(page)).toContainText('Loss review unavailable.')
    await navigate(caseRoute)
    await expect(input).toHaveValue('')
    await expect(form(page).getByLabel('Reason for posting')).toHaveValue('')
    await expect(commit(page)).toBeDisabled()
  }
  await input.fill('2')
  await describe(page,f.a.code,decision)
  const ack=page.waitForResponse(r=>r.request().method()==='POST' && r.url().endsWith('/receiving/stock-losses'))
  await commit(page).click()
  posting=(await (await ack).json()).entityId
  const correct=lossReceiptLink(f.a.id,f.position,posting)
  await expect(receipt(page)).toContainText(decision)
  for(const [bin,position] of [[f.a.id,other.positionId],[f.b.id,f.position]]) {
    const path='/api/v1/receiving/bins/'+bin+'/positions/'+position+'/posted-loss'
    let resume!:()=>void
    const gate=new Promise<void>(resolve=>{resume=resolve})
    await page.route(u=>u.pathname===path,async route=>{await gate;await route.continue()},{times:1})
    const reading=page.waitForRequest(r=>new URL(r.url()).pathname===path)
    await navigate(lossReceiptLink(bin,position,posting))
    await reading
    try {
      await expect(receipt(page)).toContainText('Reading stock-loss receipt')
      await expect(receipt(page)).not.toContainText(decision)
      await expect(receipt(page).getByRole('listitem')).toHaveCount(0)
    } finally {resume()}
    await expect(receipt(page)).toContainText('Posting receipt unavailable.')
    await navigate(correct)
    await expect(receipt(page)).toContainText(decision)
  }
})

test('a posted shortage unblocks its remaining stock on return to the same bin after a complete case read', async ({ page }) => {
  const f = await fixture(page)
  const countsPath = '/api/v1/receiving/bins/' + f.a.id + '/counts'
  const initial = await page.request.get(countsPath)
  const sibling = ((await initial.json()) as CountSnapshot).positions.find(p => p.purchaseId === f.unknown.purchaseId)!
  await f.write('receiving/counts', sibling.purchaseRevision, {
    purchaseId: sibling.purchaseId, positionId: sibling.positionId, locationId: f.a.id,
    confirmedLocationCode: f.a.code, countedQuantity: 2, reason: 'Second position needs a separate review'
  })
  await page.getByRole('button', { name: 'Inspect ' + f.a.name }).click()
  const layers = page.getByRole('list', { name: 'Receipt layers in this bin' })
  const layer = (purchase: string) => layers.getByRole('listitem').filter({ hasText: purchase })
  const paid = layer(f.paid.purchase)
  const other = layer(f.unknown.purchase)
  const transfer = paid.getByRole('button', { name: /Transfer stock/ })
  const otherTransfer = other.getByRole('button', { name: /Transfer stock/ })
  await expect(other).toContainText('Open shortage')
  await paid.getByRole('link', { name: /Count stock/ }).click()
  const count = page.getByRole('dialog', { name: 'Count stock', exact: true })
  await count.getByLabel('Confirm physical bin code').fill(f.a.code)
  await count.getByLabel('Witnessed quantity').fill('5')
  await count.getByLabel('Reason for count').fill('One missing from the paid delivery')
  await count.getByRole('button', { name: 'Record observation' }).click()
  await page.getByText('Review stock adjustment', { exact: true }).click()
  await page.getByRole('link', { name: 'Choose reductions and post stock loss' }).click()
  await describe(page, f.a.code, 'Record the single missing unit')
  await commit(page).click()
  await expect(receipt(page)).toContainText('Record the single missing unit')

  const wholeBin = (url: URL) => url.pathname === countsPath && !url.searchParams.has('positionId') &&
    !url.searchParams.has('observationId') && !url.searchParams.has('commandId')
  let attempt = 0
  await page.route(wholeBin, async route => {
    if (++attempt === 2) { await route.abort(); return }
    const response = await route.fetch()
    await route.fulfill({ response, json: { ...await response.json(), complete: false, shortages: [] } })
  })
  await receipt(page).getByRole('button', { name: 'Close stock loss' }).click()
  await expect(paid).toContainText('5 in bin')
  await expect(page.getByText(/Partial shortage read/)).toBeVisible()
  for (const row of [paid, other]) await expect(row).toContainText('Open shortage')
  await expect(transfer).toBeDisabled()
  await page.getByRole('button', { name: 'Retry shortage read' }).click()
  await expect(page.getByText(/Open shortages could not be verified/)).toBeVisible()
  await expect(paid).toContainText('Open shortage')
  await expect(transfer).toBeDisabled()
  await page.unroute(wholeBin)
  await page.getByRole('button', { name: 'Retry shortage read' }).click()
  await expect(transfer).toBeEnabled()
  await expect(paid).not.toContainText('Open shortage')
  await expect(other).toContainText('Open shortage')
  await expect(otherTransfer).toBeDisabled()

  await transfer.click()
  const move = page.getByRole('dialog', { name: 'Transfer stock', exact: true })
  await move.getByRole('combobox', { name: 'Destination bin', exact: true }).selectOption(f.b.id)
  await move.getByLabel('Confirm source bin code').fill(f.a.code)
  await move.getByLabel('Confirm destination bin code').fill(f.b.code)
  await move.getByLabel('Count moved').fill('1')
  await move.getByLabel('Reason', { exact: true }).fill('Move one remaining unbacked unit')
  await move.getByRole('button', { name: 'Confirm transfer' }).click()
  await expect(move).toHaveCount(0)
  await expect(paid).toContainText('4 in bin')
  await expect(page.getByRole('list', { name: 'Movements involving this bin' })).toContainText('Move one remaining unbacked unit')
  await expect(transfer).toBeEnabled()

  // Another operator reopens a shortage after this bin read and before the transfer preflight.
  await f.write('receiving/counts', 8, { purchaseId: f.paid.purchaseId, positionId: f.position, locationId: f.a.id,
    confirmedLocationCode: f.a.code, countedQuantity: 3, reason: 'A later witnessed shortage' })
  await transfer.click()
  await expect(paid).toContainText('Open shortage')
  await expect(transfer).toBeDisabled()
  await expect(move).toHaveCount(0)
  await expect(otherTransfer).toBeDisabled()
})
