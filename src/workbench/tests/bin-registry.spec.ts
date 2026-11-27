import {expect,type Page} from '@playwright/test'
import {test} from './trusted'
import {audit,fixture,stock} from './bin-fixture'
import {registryLink} from '../src/binLabels'
import {countPositionLink,partOutLink} from '../src/countLinks'

const panel=(page:Page)=>page.getByRole('region',{name:'Bin label registry'})
const lookup=(page:Page)=>page.getByRole('form',{name:'Physical label lookup'})
async function search(page:Page,label:string){
  await lookup(page).getByLabel('Scan or type physical label').fill(label)
  const find=lookup(page).getByRole('button',{name:'Find bin'})
  await expect(find).toBeEnabled();await find.press('Enter')
}
async function archiveForm(page:Page,label:string){
  const p=panel(page)
  await p.getByRole('button',{name:'Review archive',exact:true}).click()
  await p.getByLabel('Confirm physical bin label').fill(label)
  await p.getByLabel('Reason for archiving').fill('Training aisle closed')
  await p.getByRole('checkbox').check()
}
test('find a bin by either label, recover registration, archive reviewed occupancy and count or transfer stock out',async({page},info)=>{
  test.setTimeout(90_000)
  const f=await fixture(page),alias='Tray-'+f.a.code
  await search(page,f.a.code.toLowerCase())
  await expect(lookup(page)).toContainText('Matched canonical code '+f.a.code)
  await lookup(page).getByRole('link').press('Enter')
  const p=panel(page)
  await p.getByRole('button',{name:'Register alternate label'}).click()
  await p.getByLabel('New physical label').fill(alias)
  let original=''
  await page.route('**/api/v1/locations/labels',async route=>{
    original=route.request().postData()!
    const saved=await route.fetch();expect(saved.status()).toBe(200);await saved.dispose()
    await route.abort('connectionreset')
  },{times:1})
  await p.getByRole('button',{name:'Save alternate label'}).click()
  const pending=page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText(alias)
  await page.reload()
  const again=page.waitForRequest(r=>r.url().endsWith('/locations/labels') && r.method()==='POST')
  await pending.getByRole('button',{name:'Retry command'}).click()
  expect((await again).postData()).toBe(original)
  await page.getByRole('link',{name:'View bin registry receipt'}).click()
  await expect(p.getByRole('heading',{name:'Alternate label registered'})).toBeVisible()
  const labelReceipt=page.url()
  await page.goto('/#locations')
  await search(page,alias.toUpperCase())
  await expect(lookup(page)).toContainText('Matched alternate label '+alias)
  await lookup(page).getByRole('link').click()
  await expect(p).toContainText('9 physical units · 2 occupied stock positions')
  await archiveForm(page,alias.toLowerCase())
  await page.setViewportSize({width:320,height:740})
  await expect(p).toContainText('9 units across 2 occupied positions will stay in this bin.')
  await p.getByRole('heading',{name:'Archive this bin'}).evaluate(el=>el.scrollIntoView({block:'start'}))
  await page.screenshot({path:info.outputPath('bin-archive-narrow.png')})
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true)
  await audit(page)
  await page.route('**/api/v1/locations/archives',async route=>{
    await f.write('receiving/putaway',3,{purchaseId:f.unknown.purchaseId,deliveryId:f.unknown.deliveryId,catalogId:f.catalogId,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:1})
    const result=await route.fetch();expect(result.status()).toBe(409);expect((await result.json()).code).toBe('location_archive_stock_changed')
    await route.fulfill({response:result});await result.dispose()
  },{times:1})
  await p.getByRole('button',{name:'Confirm archive'}).click()
  await expect(p.getByRole('button',{name:'Review updated bin'})).toBeVisible()
  await p.getByRole('button',{name:'Review updated bin'}).click()
  await expect(p).toContainText('10 units across 3 occupied positions will stay in this bin.')
  await expect(p.getByRole('button',{name:'Confirm archive'})).toBeDisabled()
  await p.getByLabel('Confirm physical bin label').fill(alias.toLowerCase())
  await p.getByLabel('Reason for archiving').fill('Training aisle closed')
  await p.getByRole('checkbox').check()
  await p.getByRole('button',{name:'Confirm archive'}).click()
  await expect(p.getByRole('heading',{name:'Bin archived'})).toBeVisible()
  const archiveReceipt=page.url()
  await page.reload()
  await expect(p.getByText('Recorded physical label',{exact:true}).locator('+ dd')).toHaveText(alias)
  await expect(p.getByText('Units at archive',{exact:true}).locator('+ dd')).toHaveText('10')
  await p.getByRole('link',{name:'Inspect bin stock'}).press('Enter')
  const compactBin=page.getByRole('dialog',{name:f.a.name})
  await expect(compactBin).toBeVisible()
  await expect.poll(()=>compactBin.evaluate(el=>el.contains(document.activeElement))).toBe(true)
  await page.goto('/'+countPositionLink(f.a.id,f.position))
  const count=page.getByRole('button',{name:'Record observation'})
  await page.getByLabel('Confirm physical bin code').fill(alias.toLowerCase())
  await page.getByLabel('Witnessed quantity').fill('6')
  await page.getByLabel('Reason for count').fill('Count after shelving change')
  await count.click();await expect(page.getByText('Witnessed in '+alias,{exact:false})).toBeVisible()
  await page.setViewportSize({width:1440,height:900})
  await page.goto('/#locations/'+f.a.id)
  const layer=stock(page).getByRole('listitem').filter({hasText:f.paid.purchase})
  await layer.getByRole('button',{name:/Transfer stock/}).click()
  const transfer=page.getByRole('dialog',{name:'Transfer stock'})
  await transfer.getByRole('combobox',{name:'Destination bin'}).selectOption(f.b.id)
  await transfer.getByLabel('Confirm source bin code').fill(alias.toLowerCase())
  await transfer.getByLabel('Confirm destination bin code').fill(f.b.code.toLowerCase())
  await transfer.getByLabel('Count moved').fill('1')
  await transfer.getByLabel('Reason',{exact:true}).fill('Remove one loose unit')
  await transfer.getByRole('button',{name:'Confirm transfer'}).click()
  await expect(transfer).toHaveCount(0)
  await page.goto('/'+registryLink(f.a.id))
  await expect(p).toContainText('Archived · 9 physical units')
  await page.screenshot({path:info.outputPath('bin-registry-desktop.png')})
  await page.goto(archiveReceipt);await expect(p.getByText('Units at archive',{exact:true}).locator('+ dd')).toHaveText('10')
  await page.goto(labelReceipt);await expect(p.getByText('Recorded physical label',{exact:true}).locator('+ dd')).toHaveText(alias)
})

test('late archive rejects the submitted destination; all destination selectors exclude it after refresh',async({page})=>{
  const f=await fixture(page)
  await page.goto('/#receiving/'+f.unknown.purchaseId)
  const task=page.getByRole('region',{name:'Purchase workflow'})
  await task.getByRole('button',{name:'Put away',exact:true}).click()
  const dest=task.getByRole('combobox',{name:'Destination bin',exact:true})
  await dest.selectOption(f.empty.id);await task.getByLabel('Confirm bin code').fill(f.empty.code)
  await task.getByLabel('Count placed').fill('1')
  await page.route('**/api/v1/receiving/putaway',async route=>{
    await f.write('locations/archives',1,{locationId:f.empty.id,confirmedLabel:f.empty.code,reason:'Retire empty shelf',expectedPhysicalQuantity:0,expectedPositionCount:0})
    const reply=await route.fetch();expect(reply.status()).toBe(409);expect((await reply.json()).code).toBe('location_destination_archived')
    await route.fulfill({response:reply});await reply.dispose()
  },{times:1})
  await task.getByRole('button',{name:'Confirm putaway'}).click()
  await expect(page.getByRole('alert').first()).toContainText(/archived/i)
  await expect(page.getByRole('region',{name:'Pending confirmations'})).toHaveCount(0)
  await expect(dest.locator('option[value="'+f.empty.id+'"]')).toHaveCount(0)
  await page.goto('/#locations/'+f.a.id)
  await stock(page).getByRole('button',{name:/Transfer stock from PAID/}).click()
  const transfer=page.getByRole('dialog',{name:'Transfer stock'})
  const target=transfer.getByRole('combobox',{name:'Destination bin'})
  await expect(target.locator('option[value="'+f.b.id+'"]')).toHaveCount(1)
  await expect(target.locator('option[value="'+f.empty.id+'"]')).toHaveCount(0)
  await page.goto('/'+partOutLink(f.position))
  await page.getByLabel('Input units consumed').fill('1')
  const output=page.getByRole('combobox',{name:'Output bin',exact:true})
  await expect(output.locator('option[value="'+f.b.id+'"]')).toHaveCount(1)
  await expect(output.locator('option[value="'+f.empty.id+'"]')).toHaveCount(0)
})

test('an interrupted later registry page fences a filled putaway until the entire current registry is recovered',async({page})=>{
  test.setTimeout(90_000)
  const f=await fixture(page)
  for(let i=0;i<101;i++)await f.write('locations',0,{code:'REG-'+f.a.code+'-'+i,name:'Fictional shelf '+i})
  await page.goto('/#receiving/'+f.unknown.purchaseId)
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  const task=page.getByRole('region',{name:'Purchase workflow'})
  await task.getByRole('button',{name:'Put away',exact:true}).click()
  await task.getByRole('combobox',{name:'Destination bin'}).selectOption(f.b.id)
  await task.getByLabel('Confirm bin code').fill(f.b.code)
  await task.getByLabel('Count placed').fill('1')
  const submit=task.getByRole('button',{name:'Confirm putaway'})
  await expect(submit).toBeEnabled()
  await page.route(u=>u.pathname.endsWith('/locations/registry') && u.searchParams.has('after'),async route=>{
    const actual=await route.fetch();await route.fulfill({json:{...await actual.json(),complete:false}});await actual.dispose()
  },{times:1})
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await expect(page.getByText(/bin registry read is incomplete/i)).toBeVisible()
  await expect(submit).toBeDisabled();await expect(task.getByLabel('Count placed')).toHaveValue('1')
  await page.getByRole('button',{name:'Refresh bin registry'}).click()
  await expect(submit).toBeEnabled()
  await submit.click()
  await expect(page.getByText(/Saved Putaway to/)).toBeVisible()
})

test('a size-rejected archive retry must reconcile the original occupancy witness in the issuing bin before clearing the journal',async({page})=>{
  const f=await fixture(page)
  await page.goto('/'+registryLink(f.a.id))
  await archiveForm(page,f.a.code.toLowerCase())
  let command='',eventId=''
  const post='**/api/v1/locations/archives'
  await page.route(post,async route=>{
    command=route.request().postData()!
    const result=await route.fetch();expect(result.ok()).toBe(true)
    eventId=(await result.json()).entityId
    await result.dispose();await route.abort('connectionreset')
  },{times:1})
  await panel(page).getByRole('button',{name:'Confirm archive'}).click()
  const pending=page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText('Archive bin '+f.a.code.toLowerCase())
  await page.reload()
  await page.route(post,async route=>{
    expect(route.request().postData()).toBe(command)
    await route.fulfill({status:413,json:{code:'request_too_large',message:'Application size limit',correlationId:JSON.parse(command).commandId}})
  },{times:2})
  await page.route(u=>u.pathname.endsWith('/'+f.a.id+'/registry/receipts') && u.searchParams.has('commandId'),async route=>{
    const result=await route.fetch(),actual=await result.json()
    await route.fulfill({json:{...actual,event:{...actual.event,physicalQuantity:0}}});await result.dispose()
  },{times:1})
  await pending.getByRole('button',{name:'Retry command'}).click()
  await expect(page.getByRole('alert').first()).toContainText('verify the original command')
  await expect(pending).toBeVisible()
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await pending.getByRole('button',{name:'Retry command'}).click()
  await expect(page.getByRole('link',{name:'View bin registry receipt'})).toHaveAttribute('href',registryLink(f.a.id,eventId))
  await expect(pending).toHaveCount(0)
})

test('a slow lookup survives a register refresh and shows a later archive without rescanning',async({page})=>{
  const f=await fixture(page)
  const label='Physical-'+f.empty.code
  await f.write('locations/labels',1,{locationId:f.empty.id,label})
  let arrived!:()=>void,releaseFirst!:()=>void,again!:()=>void,releaseSecond!:()=>void
  const started=new Promise<void>(resolve=>{arrived=resolve}),first=new Promise<void>(resolve=>{releaseFirst=resolve})
  const restarted=new Promise<void>(resolve=>{again=resolve}),second=new Promise<void>(resolve=>{releaseSecond=resolve})
  let reads=0
  await page.route(u=>u.pathname.endsWith('/locations/lookup') && u.searchParams.get('label')===label,async route=>{
    reads++
    if(reads===1){
      const reply=await route.fetch();arrived();await first;await route.fulfill({response:reply});await reply.dispose()
    } else {
      again();await second;await route.continue()
    }
  })
  await search(page,label);await started
  const finished=page.waitForResponse(r=>new URL(r.url()).pathname.endsWith('/workspace'))
  await page.getByRole('button',{name:'Refresh',exact:true}).click();await finished
  await expect(page.getByText('Connected',{exact:true})).toBeVisible()
  releaseFirst();await restarted
  try{
    await expect(lookup(page)).toContainText('Matched alternate label '+label)
    await expect(lookup(page)).toContainText('Active')
    await f.write('locations/archives',2,{locationId:f.empty.id,confirmedLabel:label,reason:'End of training',expectedPhysicalQuantity:0,expectedPositionCount:0})
  }finally{releaseSecond()}
  await expect(lookup(page)).toContainText('Archived')
  await expect(lookup(page).getByRole('link')).toHaveAttribute('href',registryLink(f.empty.id))
  await lookup(page).getByLabel('Scan or type physical label').fill(f.b.code)
  await expect(lookup(page).getByRole('link')).toHaveCount(0)
})

test('a putaway confirmed by an alternate label replays once after its destination is archived',async({page})=>{
  const f=await fixture(page),alias='Rack-'+f.empty.code
  await f.write('locations/labels',1,{locationId:f.empty.id,label:alias})
  await page.goto('/#receiving/'+f.unknown.purchaseId)
  const task=page.getByRole('region',{name:'Purchase workflow'})
  await task.getByRole('button',{name:'Put away',exact:true}).click()
  await task.getByRole('combobox',{name:'Destination bin'}).selectOption(f.empty.id)
  await task.getByLabel('Confirm bin code').fill(alias.toLowerCase())
  await task.getByLabel('Count placed').fill('1')
  let body='',receiptId=''
  await page.route('**/api/v1/receiving/putaway',async route=>{
    body=route.request().postData()!
    expect(JSON.parse(body).confirmedLocationCode).toBe(alias.toLowerCase())
    const actual=await route.fetch();expect(actual.status()).toBe(200);receiptId=(await actual.json()).entityId
    await actual.dispose();await route.abort('connectionreset')
  },{times:1})
  await task.getByRole('button',{name:'Confirm putaway'}).click()
  const pending=page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText(alias.toLowerCase())
  await f.write('locations/archives',2,{locationId:f.empty.id,confirmedLabel:alias.toUpperCase(),reason:'Tagged training shelf',expectedPhysicalQuantity:1,expectedPositionCount:1})
  await page.reload()
  const replay=page.waitForResponse(r=>r.url().endsWith('/receiving/putaway') && r.request().method()==='POST')
  await pending.getByRole('button',{name:'Retry command'}).click()
  const result=await replay
  expect(result.status()).toBe(200);expect(result.request().postData()).toBe(body);expect((await result.json()).entityId).toBe(receiptId)
  await expect(page.getByText(/Saved Putaway to/)).toBeVisible()
  await page.goto('/#locations/'+f.empty.id)
  await expect(stock(page)).toContainText('Bin code at putaway '+alias)
  const summary=await (await page.request.get('/api/v1/locations/'+f.empty.id+'/registry')).json()
  expect(summary.bin).toMatchObject({archived:true,physicalQuantity:1,positionCount:1})
})

test('lookup rejects unclaimed and duplicate labels; label and event history page independently and exact receipts stay scoped',async({page})=>{
  test.setTimeout(90_000)
  const f=await fixture(page),prefix='Tag-'+f.b.code
  for(let i=0;i<26;i++)await f.write('locations/labels',i+1,{locationId:f.b.id,label:prefix+'-'+String(i).padStart(2,'0')})
  await search(page,'Missing-'+f.a.code)
  await expect(lookup(page)).toContainText(/No bin has that registered label/i)
  await page.goto('/'+registryLink(f.a.id))
  const p=panel(page)
  await p.getByRole('button',{name:'Register alternate label'}).click()
  await p.getByLabel('New physical label').fill((prefix+'-00').toLowerCase())
  await p.getByRole('button',{name:'Save alternate label'}).click()
  await expect(p.getByRole('alert')).toContainText(/already registered/i)
  await expect(page.getByRole('region',{name:'Pending confirmations'})).toHaveCount(0)
  await page.goto('/'+registryLink(f.b.id))
  const labels=p.getByRole('region',{name:'Registered physical labels'})
  const events=p.getByRole('region',{name:'Bin history'})
  await expect(labels.getByRole('listitem')).toHaveCount(25);await expect(events.getByRole('listitem')).toHaveCount(25)
  await labels.getByRole('button',{name:'More labels'}).press('Enter')
  await expect(labels.getByRole('listitem')).toHaveCount(2);await expect(labels.getByRole('heading')).toBeFocused()
  await expect(events.getByRole('listitem')).toHaveCount(25)
  await events.getByRole('button',{name:'Older bin events'}).press('Enter')
  await expect(events.getByRole('listitem')).toHaveCount(2)
  await events.getByRole('link').first().click()
  await expect(p.getByText('Receipt ID',{exact:true})).toBeVisible()
  const id=page.url().split('/').at(-1)!
  await page.evaluate(hash=>{location.hash=hash},registryLink(f.a.id,id))
  await expect(p).toContainText(/No matching registry event/i)
  await expect(p.getByText('Receipt ID',{exact:true})).toHaveCount(0)
})
