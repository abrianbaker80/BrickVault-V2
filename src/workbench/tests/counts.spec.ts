import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture, open, purchases, stock } from './bin-fixture'
import type { CountSnapshot } from '../src/api'
import { countPositionLink, countReceiptLink } from '../src/countLinks'

const countForm = (page: Page) => page.getByRole('dialog', { name: 'Count stock' })
const countReceipt = (page: Page) => page.getByRole('dialog', { name: 'Count receipt' })
const retry = (page: Page) => page.getByRole('button', { name: 'Retry command' })
const post = '**/api/v1/receiving/counts'
async function fill(page: Page, code: string, quantity: number, reason: string) {
  const form = countForm(page)
  await form.getByLabel('Confirm physical bin code').fill(code)
  await form.getByLabel('Witnessed quantity').fill(String(quantity))
  await form.getByLabel('Reason for count').fill(reason)
}
test('a focused bin and its search survive opening and closing count details', async ({page}) => {
  const f = await fixture(page)
  await page.setViewportSize({width:320,height:800})
  const search = page.getByRole('searchbox',{name:'Search locations'})
  await search.fill(f.a.code)
  const lookup = page.getByRole('form',{name:'Physical label lookup'})
  await lookup.getByLabel('Scan or type physical label').fill(f.a.code.toLowerCase())
  await lookup.getByRole('button',{name:'Find bin'}).press('Enter')
  await expect(lookup).toContainText('Matched canonical code '+f.a.code)
  await expect(lookup).toContainText('Active')
  const inspect = page.getByRole('button',{name:'Inspect '+f.a.name,exact:true})
  await inspect.focus()
  await inspect.press('Enter')
  const entry = stock(page).getByRole('link',{name:'Count stock for '+f.paid.purchase+' · '+f.paid.delivery})
  await entry.press('Enter')
  await expect(countForm(page)).toBeVisible()
  await expect(page.getByRole('table',{name:'Storage locations',includeHidden:true})).toHaveCount(0)
  await f.write('locations/archives',1,{locationId:f.a.id,confirmedLabel:f.a.code,expectedPhysicalQuantity:9,
    expectedPositionCount:2,reason:'Archive while the operator is viewing count details'})
  await countForm(page).getByRole('button',{name:'Close count details'}).press('Enter')
  const details = page.getByRole('dialog',{name:f.a.name,exact:true})
  await expect(details).toBeVisible()
  await details.getByRole('button',{name:'Close details'}).press('Enter')
  await expect(search).toHaveValue(f.a.code)
  await expect(lookup.getByLabel('Scan or type physical label')).toHaveValue(f.a.code.toLowerCase())
  await expect(lookup).toContainText('Matched canonical code '+f.a.code)
  await expect(lookup).toContainText('Archived')
  await expect(inspect).toBeFocused()
})

test('switching workspace after an accepted count cannot open its observation in the other workspace', async ({page}) => {
  const f = await fixture(page)
  const position = countPositionLink(f.a.id,f.position)
  await page.goto('/'+position)
  await fill(page,f.a.code,6,'Count in issuing workspace')
  const newWorkspace = randomUUID()
  let submitted = false, changed = false
  const observationReads: string[] = []
  page.on('request',request=>{
    const url = new URL(request.url())
    if (request.method()==='POST' && url.pathname.endsWith('/receiving/counts')) submitted=true
    if (submitted && url.searchParams.has('observationId')) observationReads.push(url.pathname)
  })
  await page.route('**/api/v1/session',async route=>{
    const reply=await route.fetch()
    try {
      if (submitted) {
        changed=true
        await route.fulfill({response:reply,json:{...await reply.json(),workspaceId:newWorkspace}})
      } else await route.fulfill({response:reply})
    } finally {await reply.dispose()}
  })
  const saved=page.waitForResponse(r=>r.request().method()==='POST' && new URL(r.url()).pathname.endsWith('/receiving/counts'))
  const refreshed=page.waitForResponse(r=>changed && new URL(r.url()).pathname.endsWith('/workspace'))
  await countForm(page).getByRole('button',{name:'Record observation'}).press('Enter')
  const reply=await saved
  expect(reply.status()).toBe(200)
  await refreshed
  await expect(page.getByText('Connected',{exact:true})).toBeVisible()
  await expect(page).toHaveURL(url=>url.hash===position)
  const receipt=await reply.json()
  // Only the session scope changes in this fixture. The genuine position history can still list the saved observation.
  const status=page.getByRole('status',{includeHidden:true})
  await expect(status.filter({hasText:receipt.correlationId})).toHaveCount(0)
  await expect(status.getByRole('link',{name:'View count receipt',includeHidden:true})).toHaveCount(0)
  await expect(countReceipt(page)).toHaveCount(0)
  expect(observationReads).toEqual([])
  const original=await page.request.get('/api/v1/receiving/bins/'+f.a.id+'/counts?commandId='+receipt.commandId)
  expect(original.status()).toBe(200)
  expect((await original.json()).observations).toHaveLength(1)
})

test('a refreshed directory preserves the original in-flight exact count receipt for an unchanged bin', async ({page}) => {
  const f=await fixture(page)
  const reason='Six pieces counted before a directory refresh'
  const observation=await f.write('receiving/counts',5,{purchaseId:f.paid.purchaseId,positionId:f.position,
    locationId:f.a.id,confirmedLocationCode:f.a.code,countedQuantity:6,reason})
  const exact=(url:URL)=>url.pathname==='/api/v1/receiving/bins/'+f.a.id+'/counts' && url.searchParams.get('observationId')===observation
  let reads=0, arrived!:()=>void, release!:()=>void
  const started=new Promise<void>(resolve=>{arrived=resolve}), resume=new Promise<void>(resolve=>{release=resolve})
  await page.route(exact,async route=>{
    if (++reads===1) {arrived();await resume}
    await route.continue()
  })
  await page.goto('/'+countReceiptLink(f.a.id,observation))
  await started
  try {
    const total=page.locator('.metrics > div').filter({hasText:'Locations'}).locator('strong')
    const previous=Number(await total.textContent())
    await f.write('locations',0,{code:'NEW-'+f.a.code,name:'Unrelated bin added during the exact receipt read'})
    await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))
    await expect(total).toHaveText(String(previous+1))
  } finally {release()}
  await expect(countReceipt(page)).toContainText(reason)
  expect(reads).toBe(1)
})

test('a witnessed zero opens a shortage and a later surplus remains separate from ledger and existing backing', async ({ page, context }, info) => {
  const f = await fixture(page)
  await page.setViewportSize({width:320, height:720})
  await open(page, f.a)
  const row = stock(page).getByRole('list', {name:'Receipt layers in this bin'}).getByRole('listitem').filter({hasText:f.paid.purchase})
  await row.getByRole('link', {name:/Count stock/}).click()
  const form = countForm(page)
  await expect(form).toContainText('6 in the ledger · 3 backed by offers')
  await expect(form.getByLabel('Witnessed quantity')).toHaveValue('')
  await fill(page, f.b.code, 0, 'Empty tray on first count')
  const save = form.getByRole('button', {name:'Record observation'})
  await save.click()
  await expect(form.getByRole('alert')).toContainText('Enter the code from this physical bin label.')
  await fill(page, f.a.code, 0, 'Empty tray on first count')
  await expect(form.getByRole('alert')).toHaveCount(0)
  await context.setOffline(true)
  await expect(save).toBeDisabled()
  await expect(form).toContainText('Showing the last count read.')
  await context.setOffline(false)
  await expect(save).toBeEnabled()
  await page.screenshot({path:info.outputPath('count-mobile.png')})
  expect(await form.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await audit(page)
  let original = '', id = '', correlation = ''
  await page.route(post, async route => {
    original = route.request().postData()!
    const response = await route.fetch()
    expect(response.ok()).toBe(true)
    const saved = await response.json()
    id = saved.entityId
    correlation = saved.correlationId
    await response.dispose()
    await route.abort('connectionreset')
  }, {times:1})
  await save.click()
  await expect(page.getByRole('region', {name:'Pending confirmations'})).toContainText('Stock count of 0 in ' + f.a.code)
  await page.reload()
  const replay = page.waitForRequest(r => r.method() === 'POST' && r.url().endsWith('/receiving/counts'))
  await retry(page).click()
  expect((await replay).postData()).toBe(original)
  await page.getByRole('link', {name:'View count receipt'}).click()
  const receipt = countReceipt(page)
  await expect(page).toHaveURL(new RegExp(countReceiptLink(f.a.id, id) + '$'))
  await expect(receipt).toContainText('Observation only. No stock adjustment posted.')
  await expect(receipt.locator('dd')).toHaveText(['0','6','3','6','0'])
  const shortage = receipt.getByRole('region', {name:'Open shortage'})
  await expect(shortage).toContainText('6 missing at opening · 6 missing on latest count')
  await expect(shortage).toContainText('Existing holds keep their priority until the owner selects a hold and quantity to release.')
  await receipt.getByText('Receipt identifiers', {exact:true}).click()
  await expect(receipt).toContainText('Correlation ID ' + correlation)
  await page.reload()
  await expect(receipt).toContainText('Empty tray on first count')
  await receipt.getByRole('link', {name:'See counts for this position'}).click()
  await expect(form.getByRole('listitem')).toHaveCount(1)
  await fill(page, f.a.code, 8, 'Two unidentified pieces in recount')
  await save.click()
  await expect(receipt.locator('dd')).toHaveText(['8','6','3','0','2'])
  await expect(shortage).toContainText('6 missing at opening · 0 missing on latest count')
  await expect(receipt).toContainText('Surplus units await identification')
  await page.setViewportSize({width:1440, height:900})
  await expect(receipt).toBeVisible()
  await page.screenshot({path:info.outputPath('count-receipt-desktop.png')})
  await audit(page)
  await receipt.getByRole('button', {name:'Close count details'}).click()
  await expect(row.getByRole('button', {name:/Transfer stock/})).toBeDisabled()
  const [purchase] = await purchases(page,[f.paid.purchaseId])
  expect(purchase.balances.physicalOnHand).toBe(8)
  expect(purchase.offers[0].backedQuantity).toBe(3)
  expect(purchase.offers[0].publishedQuantity).toBe(0)
})

test('size reconciliation looks up the original count command in its issuing bin and retains it through a partial lookup', async ({ page }) => {
  const f = await fixture(page)
  await open(page,f.a)
  await stock(page).getByRole('link',{name:'Count stock for ' + f.unknown.purchase + ' · ' + f.unknown.delivery}).click()
  await fill(page,f.a.code,3,'Confirm three counted pieces')
  let original = '', entityId = ''
  await page.route(post, async route => {
    original = route.request().postData()!
    const response = await route.fetch()
    expect(response.ok()).toBe(true)
    entityId = (await response.json()).entityId
    await response.dispose()
    await route.abort('connectionreset')
  }, {times:1})
  await countForm(page).getByRole('button', {name:'Record observation'}).click()
  await expect(retry(page)).toBeVisible()
  await page.reload()
  const command = JSON.parse(original)
  const exact = (u:URL) => u.pathname === '/api/v1/receiving/bins/' + f.a.id + '/counts' &&
    u.searchParams.get('positionId') === command.positionId && u.searchParams.get('commandId') === command.commandId
  await page.route(post, async route => {
    expect(route.request().postData()).toBe(original)
    const body = original + ' '.repeat(16384)
    const denied = await route.fetch({postData:body, headers:{...route.request().headers(), 'content-length':String(Buffer.byteLength(body))}})
    expect(denied.status()).toBe(413)
    await route.fulfill({response:denied}); await denied.dispose()
  }, {times:2})
  await page.route(exact,async route => {
    const response = await route.fetch()
    const json = await response.json()
    await route.fulfill({json:{...json,complete:false,observations:[]}})
    await response.dispose()
  },{times:1})
  await retry(page).click()
  await expect(page.getByRole('alert')).toContainText('verify the original command')
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  const exactResponse = page.waitForResponse(r => exact(new URL(r.url())))
  await retry(page).click()
  const result: CountSnapshot = await (await exactResponse).json()
  expect(result.observations.map(x => x.id)).toEqual([entityId])
  await expect(retry(page)).toHaveCount(0)
  await expect(page.getByRole('link',{name:'View count receipt'})).toHaveAttribute('href',countReceiptLink(f.a.id,entityId))
})

test('selected count history pages ten authentic receipts and isolates failed, partial, stale and expired reads', async ({ page }) => {
  const f = await fixture(page)
  const binPath = '/api/v1/receiving/bins/' + f.a.id + '/counts'
  const p = (await (await page.request.get(binPath)).json() as CountSnapshot).positions.find(p=>p.purchaseId===f.unknown.purchaseId)!
  const ids = []
  for(let i=0;i<11;i++) ids.push(await f.write('receiving/counts',p.purchaseRevision+i,{purchaseId:p.purchaseId,positionId:p.positionId,
    locationId:f.a.id,confirmedLocationCode:f.a.code,countedQuantity:3,reason:'Physical count ' + (i+1)}))
  const exact = (u:URL) => u.pathname === binPath && u.searchParams.get('positionId') === p.positionId
  const broadReads: string[] = []
  page.on('request', r => {
    const u = new URL(r.url())
    if (u.pathname.endsWith('/movements') || (u.pathname.endsWith('/counts') && !u.search)) broadReads.push(u.pathname)
  })
  await page.goto('/' + countPositionLink(f.a.id,p.positionId))
  const form = countForm(page)
  await expect(form.getByRole('listitem')).toHaveCount(10)
  await expect(form.getByRole('listitem').first()).toContainText('Physical count 11')
  const stamp = await form.getByText(/^Count read /).textContent()
  const older = form.getByRole('button',{name:'Older counts'})
  await page.route(u=>exact(u)&&u.searchParams.has('after'), r=>r.abort('connectionreset'),{times:1})
  await older.press('Enter')
  await expect(form).toContainText('Showing the last count read.')
  await expect(form.getByText(/^Count read /)).toHaveText(stamp!)
  await form.getByRole('button',{name:'Retry count read'}).click()
  await expect(form.getByRole('listitem')).toHaveCount(1)
  await expect(form.getByRole('link',{name:'View count receipt'})).toHaveAttribute('href',countReceiptLink(f.a.id,ids[0]))
  const save = form.getByRole('button',{name:'Record observation'})
  await page.route(exact, async route=>{
    const response = await route.fetch()
    const json = await response.json()
    await route.fulfill({json:{...json,complete:false,observations:[]}})
    await response.dispose()
  },{times:1})
  await form.getByRole('button',{name:'Newer counts'}).click()
  await expect(form).toContainText('last complete count read')
  await expect(save).toBeDisabled()
  await form.getByRole('button',{name:'Retry count read'}).click()
  await expect(save).toBeEnabled()
  await f.write('receiving/counts',p.purchaseRevision+11,{purchaseId:p.purchaseId,positionId:p.positionId,
    locationId:f.a.id,confirmedLocationCode:f.a.code,countedQuantity:3,reason:'Second operator counted this position'})
  await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))
  await expect(form).toContainText('The position changed.')
  await expect(save).toBeDisabled()
  await f.write('receiving/transfers',p.purchaseRevision+12,{purchaseId:p.purchaseId,sourcePlacementId:p.positionId,
    sourceLocationId:f.a.id,confirmedSourceLocationCode:f.a.code,destinationLocationId:f.b.id,confirmedDestinationLocationCode:f.b.code,
    quantity:3,reason:'Move the counted stock',backing:[]})
  await page.goto('/'+countReceiptLink(f.a.id,ids[0]))
  await expect(countReceipt(page)).toContainText('Physical count 1')
  await expect(countReceipt(page)).toContainText('This position has no current stock in this bin.')
  await expect(countReceipt(page).locator('dd')).toHaveText(['3','3','0','0','0'])
  await page.goto('/'+countReceiptLink(f.b.id,ids[0]))
  await expect(countReceipt(page)).toContainText('No count receipt with this ID')
  await expect(countReceipt(page)).not.toContainText('Physical count')
  await page.route(exact,r=>r.fulfill({status:401,json:{code:'sign_in_required',message:'Sign in',correlationId:randomUUID()}}),{times:1})
  await page.goto('/'+countPositionLink(f.a.id,p.positionId))
  await expect(page.getByLabel('Password')).toBeVisible()
  await expect(countForm(page)).toHaveCount(0)
  expect(broadReads).toEqual([])
})
