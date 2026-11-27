import { expect, type APIResponse, type Locator, type Page, type Route } from '@playwright/test'
import { test } from './trusted'
import { audit, duringBackgroundRead, endpoint, fixture, open, purchases, stock, totals } from './bin-fixture'
import type { BinTransferSnapshot } from '../src/api'

const transfer = (page: Page) => page.getByRole('dialog', { name: 'Transfer stock' })
const layers = (page: Page) => stock(page).getByRole('list', { name: 'Receipt layers in this bin' }).getByRole('listitem')
const movements = (page: Page) => stock(page).getByRole('list', { name: 'Movements involving this bin' }).getByRole('listitem')

async function prepare(page: Page, line: Locator, source: string, destination: { id: string; code: string }, count: number, reason: string) {
  await line.getByRole('button', { name: /Transfer stock/ }).click()
  const form = transfer(page)
  await form.getByRole('combobox', { name: 'Destination bin' }).selectOption(destination.id)
  await expect(form.getByLabel('Confirm source bin code')).toHaveValue('')
  await expect(form.getByLabel('Confirm destination bin code')).toHaveValue('')
  await form.getByLabel('Confirm source bin code').fill(source)
  await form.getByLabel('Confirm destination bin code').fill(destination.code)
  await form.getByLabel('Count moved').fill(String(count))
  await form.getByLabel('Reason', { exact: true }).fill(reason)
  return form
}

test('a counted transfer replays the same receipt and carries the chosen offers through an onward move', async ({ page, context }, info) => {
  const f = await fixture(page)
  const offer2 = await f.secondOffer()
  const purchase = async () => (await purchases(page, [f.paid.purchaseId]))[0]
  const before = await purchase()
  await page.setViewportSize({ width: 320, height: 800 })
  await open(page, f.a)
  await totals(page, [9, 5, 4, 4])
  const form = await prepare(page, layers(page).filter({ hasText: f.paid.purchase }), f.a.code, f.b, 4, 'Combine counted stock')
  await form.getByLabel('Confirm destination bin code').fill(f.a.code)
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  await expect(form.getByRole('alert')).toContainText('both physical bin labels')
  await form.getByLabel('Confirm destination bin code').fill(f.b.code)
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  await expect(form.getByRole('alert')).toContainText('Choose backing')
  const allocation=form.getByLabel('Move backed units for offer ' + f.firstOffer)
  await allocation.focus()
  await duringBackgroundRead(page,u=>u.pathname.endsWith('/bins/'+f.a.id+'/movements'), async()=>{
    await expect(form.getByRole('button',{name:'Confirm transfer'})).toBeDisabled()
    await expect(allocation).toBeFocused()
    await page.keyboard.type('1')
    await expect(allocation).toHaveValue('1')
  })
  await expect(form.getByRole('button',{name:'Confirm transfer'})).toBeEnabled()
  await form.getByLabel('Move backed units for offer ' + offer2).fill('2')
  await expect(form).toContainText('4 units to move · 3 backed')
  await expect(form.getByRole('alert')).toHaveCount(0)
  await context.setOffline(true)
  await expect(form.getByRole('button', { name: 'Confirm transfer' })).toBeDisabled()
  await context.setOffline(false)
  await expect(form.getByRole('button', { name: 'Confirm transfer' })).toBeEnabled()
  await page.screenshot({ path: info.outputPath('transfer-mobile.png') })
  expect(await form.evaluate(n => n.scrollWidth <= n.clientWidth)).toBe(true)
  await audit(page)

  let sent = '', correlation = ''
  await page.route('**/api/v1/receiving/transfers', async route => {
    sent = route.request().postData()!
    const result = await route.fetch()
    expect(result.ok()).toBe(true)
    correlation = (await result.json()).correlationId
    await result.dispose()
    await route.abort('connectionreset')
  }, { times: 1 })
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  const pending = page.getByRole('region', { name: 'Pending confirmations' })
  await expect(pending).toContainText(f.a.code + ' to ' + f.b.code)
  await page.reload()
  await expect(pending).toContainText(JSON.parse(sent).commandId)
  const repeat = page.waitForRequest(req => req.url().endsWith('/receiving/transfers') && req.method() === 'POST')
  await pending.getByRole('button', { name: 'Retry command' }).click()
  expect((await repeat).postData()).toBe(sent)
  await totals(page, [5, 2, 3, 3])
  const firstMove = movements(page).filter({ hasText: 'Combine counted stock' })
  await expect(firstMove).toHaveCount(1)
  await firstMove.getByText('Movement receipt', { exact: true }).click()
  await expect(firstMove.getByText('Correlation ID ' + correlation, { exact: true })).toBeVisible()
  await expect(firstMove).toContainText('1 backed · Offer ' + f.firstOffer)
  await expect(firstMove).toContainText('2 backed · Offer ' + offer2)
  await firstMove.getByRole('button', { name: 'Inspect destination bin' }).click()
  await totals(page, [6, 3, 3, 3])
  const arrival = layers(page).filter({ hasText: 'Transferred from ' + f.a.code })
  await expect(arrival).toHaveCount(1)
  await expect(arrival).toContainText(f.paid.purchase)
  await expect(arrival).toContainText(/Whole delivery of 8 · Item cost USD\s*12\.34/)
  await page.setViewportSize({ width: 1440, height: 900 })
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const onward = await prepare(page, arrival, f.b.code, f.empty, 4, 'Move counted layer to picking')
  await onward.getByLabel('Move backed units for offer ' + f.firstOffer).fill('1')
  await onward.getByLabel('Move backed units for offer ' + offer2).fill('2')
  await onward.getByRole('button', { name: 'Confirm transfer' }).click()
  await totals(page, [2, 0, 2, 2])
  const finalMove = movements(page).filter({ hasText: 'Move counted layer to picking' })
  await expect(movements(page)).toHaveCount(2)
  await finalMove.getByRole('button', { name: 'Inspect destination bin' }).click()
  await totals(page, [4, 3, 1, 1])
  await expect(layers(page)).toContainText('Transferred from ' + f.b.code)
  await expect(layers(page)).toContainText(f.paid.delivery)
  await page.screenshot({ path: info.outputPath('transfer-desktop.png'), fullPage: true })
  const after = await purchase()
  expect(after.balances).toEqual(before.balances)
  expect(after.offers.map(o => [o.id, o.backedQuantity, o.publishedQuantity])).toEqual(
    before.offers.map(o => [o.id, o.backedQuantity, o.publishedQuantity]))
  expect(after.revision).toBe(before.revision + 2)
})

test('transfer waits for a complete source and rejects a superseded layer draft', async ({ page }) => {
  const f = await fixture(page)
  const read = await page.request.get('/api/v1/receiving/bins/' + f.a.id + '/movements')
  const snapshot: BinTransferSnapshot = await read.json()
  await page.route(endpoint(f.a), route => route.fulfill({ json: { ...snapshot, complete: false } }), { times: 1 })
  await open(page, f.a)
  const row = layers(page).filter({ hasText: f.paid.purchase })
  await expect(stock(page)).toContainText('Partial bin read')
  await expect(row.getByRole('button', { name: /Transfer stock/ })).toBeDisabled()
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  const form = await prepare(page, row, f.a.code, f.b, 1, 'Counted a loose unit')
  await f.secondOffer()
  await form.getByRole('button', { name: 'Confirm transfer' }).click()
  await expect(form).toContainText('This layer or its source changed.')
  await expect(form.getByRole('button', { name: 'Confirm transfer' })).toBeDisabled()
  await form.getByRole('button', { name: 'Cancel' }).click()
  await totals(page, [9, 5, 4, 4])
  await expect(movements(page)).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Pending confirmations' })).toHaveCount(0)
})


test('an unchanged refreshed source can open a transfer once; a changed source or partial verification cancels the same intent',async({page})=>{
  const f=await fixture(page)
  await open(page,f.a)
  const row=layers(page).filter({hasText:f.paid.purchase})
  const start=row.getByRole('button',{name:/Transfer stock/}),form=transfer(page)
  const holds=(url:URL)=>url.pathname.endsWith('/positions/'+f.position+'/holds')
  function gate(){
    let arrived!:()=>void,release!:()=>void,delivered!:()=>void
    const started=new Promise<void>(resolve=>{arrived=resolve}),resume=new Promise<void>(resolve=>{release=resolve})
    const finished=new Promise<void>(resolve=>{delivered=resolve})
    const handle=async(route:Route)=>{
      const result=await route.fetch()
      try{arrived();await resume;await route.fulfill({response:result})}
      finally{await result.dispose();delivered()}
    }
    return {started,finished,release,handle}
  }
  let nextHolds:ReturnType<typeof gate>|undefined,nextMovement:ReturnType<typeof gate>|undefined
  let partialMovement=false
  const movement=(u:URL)=>u.pathname.endsWith('/bins/'+f.a.id+'/movements')
  await page.route(u=>holds(u)||movement(u),async route=>{
    const url=new URL(route.request().url())
    if(holds(url) && nextHolds){const held=nextHolds;nextHolds=undefined;await held.handle(route)}
    else if(movement(url) && nextMovement){const held=nextMovement;nextMovement=undefined;await held.handle(route)}
    else if(movement(url) && partialMovement){
      partialMovement=false
      const result=await route.fetch()
      await route.fulfill({json:{...await result.json(),complete:false}});await result.dispose()
    }else await route.continue()
  })
  async function pausedCheck(refresh:()=>Promise<void>){
    const held=gate();nextHolds=held
    await start.click();await held.started
    try{await refresh()}finally{held.release();await held.finished}
  }
  async function refreshSource(){
    const moved=page.waitForResponse(r=>new URL(r.url()).pathname.endsWith('/'+f.a.id+'/movements'))
    const counted=page.waitForResponse(r=>new URL(r.url()).pathname.endsWith('/'+f.a.id+'/counts'))
    await page.getByRole('button',{name:'Refresh',exact:true}).click()
    await Promise.all([moved,counted])
    await expect(stock(page).getByText('Refreshing bin stock…')).toHaveCount(0)
  }
  await pausedCheck(refreshSource)
  await expect(form).toHaveCount(1)
  await form.getByRole('combobox',{name:'Destination bin',exact:true}).selectOption(f.b.id)
  await form.getByRole('button',{name:'Cancel',exact:true}).press('Enter')

  const heldMovement=gate()
  await pausedCheck(async()=>{
    nextMovement=heldMovement
    await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')))
    await heldMovement.started
  })
  try{
    await expect(stock(page).getByText(/Verifying the transfer source/)).toBeVisible()
    await expect(form).toHaveCount(0)
  }finally{heldMovement.release();await heldMovement.finished}
  await expect(form).toHaveCount(1)
  await form.getByRole('button',{name:'Cancel',exact:true}).press('Enter')

  await pausedCheck(async()=>{await f.secondOffer();await refreshSource()})
  await expect(stock(page).getByRole('alert')).toContainText('Refresh this bin')
  await expect(form).toHaveCount(0)
  await totals(page,[9,5,4,4])
  await pausedCheck(async()=>{
    partialMovement=true
    await refreshSource()
  })
  await expect(stock(page)).toContainText('Partial bin read')
  await expect(form).toHaveCount(0)
  await expect(start).toBeDisabled()
  await expect(page.getByRole('region',{name:'Pending confirmations'})).toHaveCount(0)
})


test('a late cancelled hold check cannot open or replace a different transfer and a foreign purchase witness is rejected',async({page})=>{
  const f=await fixture(page)
  await open(page,f.a)
  const button=(purchase:string)=>layers(page).filter({hasText:purchase}).getByRole('button',{name:/Transfer stock/})
  const form=transfer(page)
  let arrived!:()=>void,release!:()=>void
  const started=new Promise<void>(resolve=>{arrived=resolve}),resume=new Promise<void>(resolve=>{release=resolve})
  const heldRead=(u:URL)=>u.pathname.endsWith('/positions/'+f.position+'/holds')
  await page.route(heldRead,async route=>{
    const result=await route.fetch()
    try{arrived();await resume;await route.fulfill({response:result})}finally{await result.dispose()}
  },{times:1})
  await button(f.paid.purchase).click();await started
  await stock(page).getByRole('button',{name:'Cancel transfer preparation'}).press('Enter')
  await button(f.unknown.purchase).click()
  try{await expect(form).toContainText(f.unknown.purchase)}finally{
    const returned=page.waitForResponse(r=>heldRead(new URL(r.url())))
    release();await returned
  }
  await expect(form).toHaveCount(1);await expect(form).toContainText(f.unknown.purchase)
  await form.getByRole('button',{name:'Cancel',exact:true}).press('Enter')
  await page.route(heldRead,async route=>{
    const response=await route.fetch()
    await route.fulfill({json:{...await response.json(),purchaseId:f.unknown.purchaseId}})
    await response.dispose()
  },{times:1})
  await button(f.paid.purchase).press('Enter')
  await expect(stock(page).getByRole('alert')).toContainText('Refresh this bin')
  await expect(form).toHaveCount(0)
  await expect(page.getByRole('region',{name:'Pending confirmations'})).toHaveCount(0)
})

test('a newer exact shortage blocks transfer immediately and its superseded witness cannot reopen a closed shortage', async ({ page }) => {
  const f = await fixture(page)
  await open(page, f.a)
  const paid = layers(page).filter({ hasText: f.paid.purchase })
  const start = paid.getByRole('button', { name: /Transfer stock/ })
  const bin = { purchaseId: f.paid.purchaseId, locationId: f.a.id, positionId: f.position, confirmedLocationCode: f.a.code }
  const holds = (u: URL) => u.pathname.endsWith('/positions/' + f.position + '/holds')
  let scope = '', shortageId = ''
  let retained: APIResponse | undefined
  await page.route(holds, async route => {
    if (retained) { await route.fulfill({ response: retained }); return }
    // The bin was verified before activation. The separate operator records a shortage before the exact holds read.
    await f.write('receiving/counts', 5, { ...bin, countedQuantity: 5, reason: 'One piece missing at inspection' })
    scope = route.request().url()
    retained = await route.fetch()
    const witness = await retained.json()
    expect(witness.purchaseRevision).toBe(6)
    expect(witness.shortageId).toBeTruthy()
    shortageId = witness.shortageId
    await route.fulfill({ response: retained })
  })
  try {
    await expect(start).toBeEnabled()
    await start.press('Enter')
    await expect(paid).toContainText('Open shortage')
    await expect(start).toBeDisabled()
    await expect(transfer(page)).toHaveCount(0)

    const observed = await f.write('receiving/counts', 6, { ...bin, countedQuantity: 6, reason: 'All six pieces witnessed' })
    await f.write('receiving/shortage-closures', 7, { ...bin, caseId: shortageId, expectedObservationId: observed, reason: 'Recount matches the ledger' })
    await page.getByRole('button', { name: 'Refresh', exact: true }).click()
    await expect(start).toBeEnabled()
    await expect(paid).not.toContainText('Open shortage')
    const oldReply = page.waitForResponse(r => r.url() === scope)
    await start.press('Enter')
    await oldReply
    await expect(stock(page).getByRole('alert')).toContainText('Refresh this bin')
    await expect(start).toBeEnabled()
    await expect(paid).not.toContainText('Open shortage')
    await expect(transfer(page)).toHaveCount(0)
  } finally {
    await page.unroute(holds)
    await retained?.dispose()
  }
  await start.press('Enter')
  await expect(transfer(page)).toContainText(f.paid.purchase)
})

async function holdNextRead(page: Page, matches: (url: URL) => boolean) {
  let arrived!: () => void, release!: () => void
  const started = new Promise<void>(resolve => { arrived = resolve })
  const resume = new Promise<void>(resolve => { release = resolve })
  await page.route(matches, async route => {
    const response = await route.fetch()
    try { arrived(); await resume; await route.fulfill({ response }) }
    finally { await response.dispose() }
  }, { times: 1 })
  return { started, release }
}

async function pressAcrossRead(page: Page, button: Locator, finishRead: () => Promise<void>) {
  await button.scrollIntoViewIfNeeded()
  const bounds = (await button.boundingBox())!
  const point = { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 }
  const hitsButton = () => button.evaluate((element, {x, y}) => element.contains(document.elementFromPoint(x, y)), point)
  expect(await hitsButton()).toBe(true)
  await page.mouse.move(point.x, point.y)
  await page.mouse.down()
  try {
    await finishRead()
    expect(await hitsButton()).toBe(true)
  } finally { await page.mouse.up() }
}

for (const width of [1440, 320]) {
  test('movement history keeps the selected bin under the pointer while reads complete at ' + width + 'px', async ({ page }) => {
    const f = await fixture(page)
    await page.setViewportSize({width, height:900})
    await f.revise()
    await f.write('locations/capacities', 1, {locationId:f.a.id, pieceLimit:6, reason:'Six pieces fit on the source shelf'})
    await page.getByRole('button', {name:'Refresh', exact:true}).click()
    const sourceRow = page.getByRole('row').filter({has:page.getByRole('button', {name:'Inspect ' + f.a.name, exact:true})})
    await expect(sourceRow).toContainText('above limit')
    await open(page, f.a)
    const line = layers(page).filter({hasText:f.unknown.purchase})
    const form = await prepare(page, line, f.a.code, f.empty, 3, 'Store this counted stock')
    let submitted = false
    page.on('request', r => {
      if (r.method() === 'POST' && new URL(r.url()).pathname.endsWith('/receiving/transfers')) submitted = true
    })
    const reread = await holdNextRead(page, u => submitted && u.pathname === '/api/v1/locations/registry')
    const cap = page.locator('.inspector dt').filter({hasText:'Piece capacity'}).locator('+ dd')
    const movement = movements(page).filter({hasText:'Store this counted stock'})
    try {
      await form.getByRole('button', {name:'Confirm transfer'}).click()
      await reread.started
      await expect(form).toHaveCount(0)
      await expect(stock(page).getByRole('heading', {name:'Stock in this bin'})).toBeFocused()
      await expect(cap).toContainText('Last confirmed')
      const destination = movement.getByRole('button', {name:'Inspect destination bin'})
      await pressAcrossRead(page, destination, async () => {
        reread.release()
        await expect(cap).not.toContainText('Last confirmed')
      })
    } finally { reread.release() }
    await totals(page, [3, 0, 3, 0])
    await expect(page.locator('#item-title')).toHaveText(f.empty.name)

    const refresh = await holdNextRead(page, u => u.pathname === '/api/v1/receiving/bins/' + f.empty.id + '/movements')
    try {
      await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
      await refresh.started
      const status = stock(page).getByText('Refreshing bin stock…')
      await expect(status).toBeVisible()
      await pressAcrossRead(page, movement.getByRole('button', {name:'Inspect source bin'}), async () => {
        refresh.release()
        await expect(status).toHaveCount(0)
      })
    } finally { refresh.release() }
    await totals(page, [6, 3, 3, 0])
    await expect(page.locator('#item-title')).toHaveText(f.a.name)
    const metric = (name: string) => page.locator('.inspector').getByText(name, {exact:true}).locator('+ dd')
    await expect(metric('Pieces present')).toHaveText('6')
    await expect(metric('Piece limit')).toHaveText('6')
    await expect(metric('Spaces remaining')).toHaveText('0')
    await expect(metric('Pieces above limit')).toHaveText('0')
  })
}

test('moving an unknown-cost layer preserves its recorded identity and leaves history on an empty source', async ({ page }) => {
  const f = await fixture(page)
  await f.revise()
  await open(page, f.a)
  const row = layers(page).filter({ hasText: f.unknown.purchase })
  await expect(row).toContainText('Placed at catalog revision 1.')
  const first = await prepare(page, row, f.a.code, f.empty, 3, 'Store counted stock')
  await expect(first).toContainText('The layer keeps its recorded catalog revision 1.')
  await first.getByRole('button', { name: 'Confirm transfer' }).click()
  const link = movements(page).filter({ hasText: 'Store counted stock' })
  await link.getByRole('button', { name: 'Inspect destination bin' }).click()
  await totals(page, [3, 0, 3, 0])
  const arrived = layers(page).filter({ hasText: f.unknown.purchase })
  await expect(arrived).toContainText('Whole delivery of 4 · Item cost Unknown (EUR)')
  await expect(arrived).toContainText('Placed at catalog revision 1.')
  const second = await prepare(page, arrived, f.empty.code, f.b, 3, 'Clear temporary bin')
  await second.getByRole('button', { name: 'Confirm transfer' }).click()
  await expect(stock(page)).toContainText('No stock recorded in this bin.')
  await expect(layers(page)).toHaveCount(0)
  await expect(movements(page)).toHaveCount(2)
  await expect(movements(page).filter({ hasText: 'Clear temporary bin' })).toContainText(f.unknown.delivery)
})
