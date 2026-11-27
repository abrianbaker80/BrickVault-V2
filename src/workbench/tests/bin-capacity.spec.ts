import {expect,type Page} from '@playwright/test'
import {test} from './trusted'
import {audit,fixture,purchases,stock} from './bin-fixture'
import {capacityLink} from '../src/binCapacity'
import {partOutLink} from '../src/countLinks'

const policy=(page:Page)=>page.getByRole('region',{name:'Bin piece limit',exact:true})
async function edit(page:Page,mode:'set'|'clear',reason:string){
  const p=policy(page)
  await p.getByRole('button',{name:'Change piece limit'}).press('Enter')
  const choice=p.getByLabel('New bin policy')
  await expect(choice).toBeFocused()
  await choice.selectOption(mode);await p.getByLabel('Reason for change').fill(reason)
}

test('set, reopen and clear a physical piece limit without moving stock; reconcile the original command and preserve its occupancy receipt',async({page},info)=>{
  const f=await fixture(page),p=policy(page)
  await page.getByRole('button',{name:'Inspect '+f.a.name}).press('Enter')
  await page.getByRole('link',{name:'Manage piece limit'}).press('Enter')
  await expect(p).toContainText('9 present · No piece limit set')
  await edit(page,'set','Short shelf measured by operator')
  await p.getByLabel('Piece limit',{exact:true}).fill('0')
  const save=p.getByRole('button',{name:'Save piece limit'})
  await expect(save).toBeDisabled()
  await p.getByLabel('Piece limit',{exact:true}).fill('8')
  await expect(p).toContainText('9 pieces already here would be 1 above')
  await page.setViewportSize({width:320,height:740})
  await p.getByLabel('New bin policy').evaluate(el=>el.scrollIntoView({block:'center'}))
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true)
  await page.screenshot({path:info.outputPath('bin-capacity-narrow.png')})
  await audit(page)
  const post='**/api/v1/locations/capacities'
  let original='',id=''
  await page.route(post,async route=>{
    original=route.request().postData()!
    const result=await route.fetch();expect(result.status()).toBe(200);id=(await result.json()).entityId
    await result.dispose();await route.abort('connectionreset')
  },{times:1})
  await save.press('Enter')
  const pending=page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText('Piece limit change')
  await page.reload()
  await page.route(post,async route=>{
    expect(route.request().postData()).toBe(original)
    await route.fulfill({status:413,json:{code:'request_too_large',message:'Application size limit',correlationId:JSON.parse(original).commandId}})
  },{times:2})
  await page.route(u=>u.pathname.endsWith('/capacity/receipts') && u.searchParams.has('commandId'),async route=>{
    const r=await route.fetch(),body=await r.json()
    await route.fulfill({json:{...body,event:{...body.event,pieceLimit:7}}});await r.dispose()
  },{times:1})
  await pending.getByRole('button',{name:'Retry command'}).click()
  await expect(page.getByRole('alert').first()).toContainText('verify the original command')
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await pending.getByRole('button',{name:'Retry command'}).click()
  await page.getByRole('link',{name:'View piece limit receipt'}).press('Enter')
  await expect(page).toHaveURL(u=>u.hash===capacityLink(f.a.id,id))
  await expect(p).toContainText('9 pieces across 2 positions')
  await expect(p).toContainText('Short shelf measured by operator')
  await p.getByRole('link',{name:'Current bin capacity'}).click()
  await expect(p).toContainText('9 present · Piece limit 8 · 0 spaces remaining · 1 above limit')
  await page.setViewportSize({width:1440,height:900});await page.screenshot({path:info.outputPath('bin-capacity-desktop.png')})
  await edit(page,'clear','Shelf replaced')
  await save.click()
  await expect(p.getByRole('heading',{name:'No piece limit set',exact:true})).toBeVisible()
  await expect(p.getByText('Previous policy',{exact:true}).locator('+ dd')).toHaveText('Piece limit 8')
  await page.goto('/'+capacityLink(f.a.id,id));await page.reload()
  await expect(p.getByRole('heading',{name:'Piece limit 8',exact:true})).toBeVisible()
  await expect(p.getByText('Previous policy',{exact:true}).locator('+ dd')).toHaveText('No piece limit set')
})

test('a competing placement takes the last spaces and confirmed putaway reports the rejected bin and allows a new destination',async({page})=>{
  const f=await fixture(page)
  await f.write('locations/capacities',1,{locationId:f.empty.id,pieceLimit:2,reason:'Two slots'})
  const other=await f.write('receiving/purchases',0,{reference:'SPACE-'+f.empty.code,source:'Sample',currency:'USD',orderedQuantity:2,pendingChargeMinor:null})
  const delivery=await f.write('receiving/deliveries',1,{purchaseId:other,reference:'Other box',quantity:2,catalogId:f.catalogId,itemCostMinor:null})
  await page.goto('/#receiving/'+f.unknown.purchaseId)
  const task=page.getByRole('region',{name:'Purchase workflow'})
  await task.getByRole('button',{name:'Put away',exact:true}).click()
  const dest=task.getByRole('combobox',{name:'Destination bin'})
  await dest.selectOption(f.empty.id);await task.getByLabel('Confirm bin code').fill(f.empty.code);await task.getByLabel('Count placed').fill('1')
  await expect(task).toContainText('0 present · Piece limit 2 · 2 spaces remaining')
  await page.route(u=>u.pathname.endsWith('/locations/registry'),async route=>{
    const result=await route.fetch(),body=await result.json()
    delete body.rows[0].capacity
    await route.fulfill({json:body});await result.dispose()
  },{times:1})
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await expect(page.getByRole('button',{name:'Refresh bin registry'})).toBeVisible()
  await expect(task.getByRole('button',{name:'Confirm putaway'})).toBeDisabled()
  await page.getByRole('button',{name:'Refresh bin registry'}).click()
  await expect(task.getByRole('button',{name:'Confirm putaway'})).toBeEnabled()
  await page.route('**/api/v1/receiving/putaway',async route=>{
    await f.write('receiving/putaway',2,{purchaseId:other,deliveryId:delivery,catalogId:f.catalogId,locationId:f.empty.id,confirmedLocationCode:f.empty.code,quantity:2})
    expect(route.request().headers()['x-bv-location-contract']).toBe('2')
    const result=await route.fetch();expect(result.status()).toBe(409);expect((await result.json()).capacity.remainingQuantity).toBe(0)
    await route.fulfill({response:result});await result.dispose()
  },{times:1})
  const confirm=task.getByRole('button',{name:'Confirm putaway'})
  await confirm.click()
  await expect(page.getByRole('alert').first()).toContainText('Bin '+f.empty.code+' has room for 0')
  await expect(confirm).toBeDisabled()
  await expect(task.getByLabel('Count placed')).toHaveValue('1')
  await expect(page.getByRole('region',{name:'Pending confirmations'})).toHaveCount(0)
  await dest.selectOption(f.b.id);await task.getByLabel('Confirm bin code').fill(f.b.code);await task.getByLabel('Count placed').fill('1');await confirm.click()
  await expect(page.getByText(/Saved Putaway to/)).toBeVisible()
  expect((await purchases(page,[other]))[0].deliveries[0].placements[0].quantity).toBe(2)
})

test('partial or stale capacity evidence fences a draft and policy history remains scoped across pages, refresh and disconnection',async({page})=>{
  const f=await fixture(page),p=policy(page)
  let first=''
  for(let rev=1;rev<=26;rev++){
    const event=await f.write('locations/capacities',rev,{locationId:f.b.id,pieceLimit:rev+3,reason:'Shelf review '+rev})
    if(rev===1)first=event
  }
  await page.goto('/'+capacityLink(f.b.id))
  const history=p.getByRole('region',{name:'Piece limit history'})
  await expect(history.getByRole('link')).toHaveCount(25)
  await history.getByRole('button',{name:'Older changes'}).press('Enter')
  await expect(history.getByRole('heading')).toBeFocused();await expect(history.getByRole('link')).toHaveCount(1)
  const refresh=page.getByRole('button',{name:'Refresh',exact:true})
  await refresh.click()
  await expect(history.getByRole('link').first()).toHaveAttribute('href',capacityLink(f.b.id,first))
  await edit(page,'set','Retain the operator draft');await p.getByLabel('Piece limit',{exact:true}).fill('30')
  const path='/locations/'+f.b.id+'/capacity'
  await page.route(u=>u.pathname.endsWith(path),async route=>{
    const r=await route.fetch();await route.fulfill({json:{...await r.json(),complete:false}});await r.dispose()
  },{times:1})
  await refresh.click()
  await expect(p).toContainText('The piece limit read is incomplete.')
  const save=p.getByRole('button',{name:'Save piece limit'})
  await expect(save).toBeDisabled()
  await expect(p.getByLabel('Piece limit',{exact:true})).toHaveValue('30')
  await page.context().setOffline(true)
  try{await expect(p).toContainText('offline');await expect(save).toBeDisabled()}
  finally{await page.context().setOffline(false)}
  await expect(save).toBeEnabled()
  await f.write('locations/capacities',27,{locationId:f.b.id,pieceLimit:35,reason:'Separate measurement'})
  await refresh.click()
  await expect(p.getByRole('alert')).toContainText('occupancy changed')
  await expect(save).toBeDisabled();await expect(history.getByRole('link')).toHaveCount(1)
  await history.getByRole('link').first().press('Enter')
  await expect(p.getByRole('heading',{name:'Piece limit 4',exact:true})).toBeVisible()
  await page.evaluate(hash=>{location.hash=hash},capacityLink(f.empty.id,first))
  await expect(p).toContainText(/not found|no matching|unknown/i)
  await expect(p.getByText('Shelf review 1',{exact:true})).toHaveCount(0)
})

test('manual part-out combines outputs and consumed input per bin; over-limit stock can move out while a full destination is fenced',async({page})=>{
  const f=await fixture(page)
  await f.write('locations/capacities',1,{locationId:f.a.id,pieceLimit:8,reason:'Already above limit'})
  await f.write('locations/capacities',1,{locationId:f.b.id,pieceLimit:3,reason:'One space left'})
  const other=await f.write('catalog',0,{itemType:'part',itemNumber:'CAP-'+f.a.code,name:'Fictional base',variant:null,colorState:'known',colorId:7,
    condition:'used',completeness:'complete',source:'Fictional stock',license:'operator-owned',mapping:'confirmed'})
  const pos=(await purchases(page,[f.unknown.purchaseId]))[0].deliveries[0].placements[0].id
  await page.goto('/'+partOutLink(pos))
  const form=page.getByRole('region',{name:'Manual part-out',exact:true})
  await form.getByLabel('Input units consumed').fill('1')
  await form.getByLabel('Confirm input bin code').fill(f.a.code)
  const output=(n:number)=>form.getByRole('group',{name:'Output '+n,exact:true})
  async function place(n:number,bin:typeof f.a){
    const row=output(n)
    await row.getByRole('combobox',{name:'Output bin',exact:true}).selectOption(bin.id)
    await row.getByLabel('Confirm output bin code').fill(bin.code)
  }
  for(let i=1;i<=2;i++){
    if(i>1)await form.getByRole('button',{name:'Add another output'}).click()
    const row=output(i)
    await row.getByRole('combobox',{name:'Output item',exact:true}).selectOption(other)
    await row.getByLabel('Output quantity').fill('1')
    await row.getByRole('combobox',{name:'Output disposition',exact:true}).selectOption('stock')
    await place(i,f.b)
  }
  const preview=form.getByRole('region',{name:'Combined output capacity'})
  await expect(preview).toContainText('Adding 2 pieces. Choose another bin')
  await form.getByRole('combobox',{name:'Discarded or lost material'}).selectOption('none')
  await form.getByLabel('Reason for part-out').fill('Counted two outputs')
  await form.getByRole('checkbox').check()
  const submit=form.getByRole('button',{name:'Record part-out'})
  await expect(submit).toBeDisabled()
  await place(2,f.a);await form.getByRole('checkbox').check()
  await expect(preview).toContainText('1 output piece and 1 input piece consumed here · net 0')
  await submit.click()
  await expect(page.getByRole('region',{name:'Part-out receipt'})).toBeVisible()
  await page.goto('/#locations/'+f.a.id)
  await stock(page).getByRole('button',{name:/Transfer stock from PAID/}).click()
  const transfer=page.getByRole('dialog',{name:'Transfer stock'})
  const dest=transfer.getByRole('combobox',{name:'Destination bin',exact:true})
  await dest.selectOption(f.b.id);await transfer.getByLabel('Confirm source bin code').fill(f.a.code)
  await transfer.getByLabel('Confirm destination bin code').fill(f.b.code);await transfer.getByLabel('Count moved').fill('1')
  await transfer.getByLabel('Reason',{exact:true}).fill('Free one shelf space')
  const confirm=transfer.getByRole('button',{name:'Confirm transfer'})
  await expect(transfer).toContainText('0 spaces remaining');await expect(confirm).toBeDisabled()
  await dest.selectOption(f.empty.id);await transfer.getByLabel('Confirm destination bin code').fill(f.empty.code)
  await confirm.click();await expect(page.getByText(/Saved Transfer/)).toBeVisible()
  await page.goto('/'+capacityLink(f.a.id));await expect(policy(page)).toContainText('8 present · Piece limit 8 · 0 spaces remaining')
})
