import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { audit, fixture } from './bin-fixture'
import { countReceiptLink, lossReceiptLink } from '../src/countLinks'
import type { PositionTimelinePage } from '../src/api'

const timeline = (page: Page) => page.getByRole('region', { name: 'Position timeline' })
const inspector = (page: Page) => page.locator('[aria-labelledby="position-title"]')
const onHand = (page: Page) => inspector(page).getByText('Physical on hand', { exact: true }).locator('+ dd')
const endpoint = (id: string) => '/api/v1/inventory/positions/' + id + '/timeline'

test('activity reconstructs a depleted position with original backing, build reservations, movement and exact count and loss receipts', async ({page}, info) => {
  const f = await fixture(page)
  const purchaseId = f.paid.purchaseId
  const base = { purchaseId, positionId: f.position, locationId: f.a.id, confirmedLocationCode: f.a.code }
  const arrival = await f.write('receiving/transfers',5,{ purchaseId, sourcePlacementId:f.position, sourceLocationId:f.a.id,
    confirmedSourceLocationCode:f.a.code, destinationLocationId:f.empty.id, confirmedDestinationLocationCode:f.empty.code,
    quantity:2, reason:'Sort two loose pieces into the second bin', backing:[] })
  const hold = await f.write('receiving/holds',6,{...base,quantity:1,purpose:'Station roof',reason:'Keep one piece for the build'})
  const observed = await f.write('receiving/counts',7,{...base,countedQuantity:0,reason:'No pieces left in the source tray'})
  await f.write('receiving/holds/release',8,{purchaseId,holdId:hold,quantity:1,shortageId:observed,
    expectedObservationId:observed, reason:'Owner released the roof piece'})
  const posting = await f.write('receiving/stock-losses',9,{...base, caseId:observed,expectedObservationId:observed,
    reason:'Post the four missing pieces',offerReductions:[{offerId:f.firstOffer,quantity:3}]})

  const from = '#inventory/' + f.position
  const first = page.waitForResponse(r=>new URL(r.url()).pathname===endpoint(f.position))
  await page.goto('/'+from)
  const result: PositionTimelinePage = await (await first).json()
  expect(result.positionId).toBe(f.position)
  expect(result.events.map(e=>e.kind)).toEqual(['stock-loss','hold-release','count','hold','transfer-out','offer-backing','putaway'])
  const panel = timeline(page)
  const rows = panel.getByRole('listitem')
  await expect(page.getByText('No units remain in this position.')).toBeVisible()
  await expect(rows).toHaveCount(7)
  await expect(rows.nth(0)).toContainText('-4 on hand')
  await expect(rows.nth(0)).toContainText('-3 backed by offers')
  await expect(rows.nth(1)).toContainText('-1 reserved for builds')
  await expect(rows.nth(2)).toContainText('0 observed · 4 in ledger at count')
  await expect(rows.nth(3)).toContainText('Station roof')
  await expect(rows.nth(4)).toContainText('From '+f.a.code+' to '+f.empty.code)
  await expect(rows.nth(5)).toContainText('+3 backed by offers')
  await expect(rows.nth(6)).toContainText('+6 on hand')
  const count = panel.getByRole('link',{name:'View count receipt'})
  const loss = panel.getByRole('link',{name:'View posted loss receipt'})
  await expect(count).toHaveAttribute('href',countReceiptLink(f.a.id,observed))
  await expect(loss).toHaveAttribute('href',lossReceiptLink(f.a.id,f.position,posting))
  await expect(page.getByRole('region',{name:'Position movement history'})).toHaveCount(0)
  await panel.getByRole('heading').evaluate(el=>el.scrollIntoView({block:'start'}))
  await page.screenshot({path:info.outputPath('timeline-desktop.png')})
  await audit(page)
  await count.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('dialog',{name:'Count receipt',exact:true})).toContainText('No pieces left in the source tray')
  await page.goBack()
  await loss.click()
  await expect(page.getByRole('dialog',{name:'Posted stock loss',exact:true})).toContainText('Post the four missing pieces')
  await page.goBack()

  const related = panel.getByRole('link',{name:'Inspect destination position'})
  await related.focus()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(arrival+'$'))
  await expect(rows).toHaveCount(1)
  await expect(rows.first()).toContainText('Transferred in')
  await expect(rows.first()).toContainText('+2 on hand')
  await expect(panel.getByRole('link',{name:'Inspect source position'})).toHaveAttribute('href',from)
  const back = page.getByRole('link',{name:'Return to previous position timeline'})
  await expect(back).toHaveAttribute('href',from)
  await back.click()
  await expect(rows).toHaveCount(7)
  await page.setViewportSize({width:320,height:800})
  const dialog = page.getByRole('dialog',{name:'Position inspector'})
  await expect(dialog).toBeVisible()
  await expect(rows).toHaveCount(7)
  expect(await dialog.evaluate(el=>el.scrollWidth<=el.clientWidth)).toBe(true)
  await panel.getByRole('heading').evaluate(el=>el.scrollIntoView({block:'start'}))
  await page.screenshot({path:info.outputPath('timeline-mobile.png')})
  await audit(page)
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
})

test('timeline paging retains confirmed evidence across partial, failed and wrong-scope reads', async ({page}) => {
  const f = await fixture(page)
  const base = { purchaseId:f.paid.purchaseId, positionId:f.position, locationId:f.a.id, confirmedLocationCode:f.a.code }
  for (let i=0;i<27;i++)
    await f.write('receiving/counts',5+i,{...base,countedQuantity:6,reason:'Shelf witness '+(i+1)})
  const path = endpoint(f.position)
  const real = page.waitForResponse(r=>new URL(r.url()).pathname===path)
  await page.goto('/#inventory/'+f.position)
  const first: PositionTimelinePage = await (await real).json()
  expect(first.nextAfter).toBeTruthy()
  const panel = timeline(page)
  const rows = panel.getByRole('listitem')
  await expect(rows).toHaveCount(25)
  const stamp = panel.getByText(/^Timeline read /)
  const prior = await stamp.textContent()
  await expect(onHand(page)).toHaveText('6')
  const positionStamp = inspector(page).getByText(/^Position read /)
  const priorPositionStamp = await positionStamp.textContent()
  const matches = (url:URL) => url.pathname===path && url.searchParams.get('after')===first.nextAfter && url.searchParams.get('limit')==='25'
  await page.route(matches,r=>r.fulfill({json:{...first,complete:false,events:[],nextAfter:null,
    position:{...first.position,layer:{...first.position.layer,quantity:999}}}}),{times:1})
  const older = panel.getByRole('button',{name:'Older events'})
  await older.focus()
  await page.keyboard.press('Enter')
  const retry = panel.getByRole('button',{name:'Retry timeline page 2'})
  await expect(retry).toBeFocused()
  await expect(panel).toContainText('Partial timeline read. Showing the last confirmed timeline page.')
  await expect(panel).not.toContainText('No events recorded')
  await expect(rows).toHaveCount(25)
  await expect(stamp).toHaveText(prior!)
  await expect(onHand(page)).toHaveText('6')
  await expect(positionStamp).toHaveText(priorPositionStamp!)
  for (const overrides of [{purchaseId:randomUUID()}, {deliveryId:randomUUID()}, {positionId:randomUUID()},
    {position:{...first.position,layer:{...first.position.layer,placementId:randomUUID(),quantity:999}}},
    {position:{...first.position,location:{...first.location,name:'Unrelated bin'}}},
    {events:[{...first.events[0],locationId:randomUUID(),reason:'Unrelated receipt'}]}]) {
    await page.route(matches,r=>r.fulfill({json:{...first,...overrides}}),{times:1})
    const done = page.waitForResponse(r=>matches(new URL(r.url())))
    await retry.click()
    await done
    await expect(retry).toBeFocused()
    await expect(panel).toContainText('could not verify the server response')
    await expect(panel).not.toContainText('Unrelated receipt')
    await expect(onHand(page)).toHaveText('6')
    await expect(positionStamp).toHaveText(priorPositionStamp!)
    await expect(stamp).toHaveText(prior!)
  }
  await page.route(matches,r=>r.abort('connectionreset'),{times:1})
  await retry.click()
  await expect(panel).toContainText('Showing the last confirmed timeline page.')
  await expect(retry).toBeFocused()
  await f.write('receiving/transfers',32,{purchaseId:f.paid.purchaseId,sourcePlacementId:f.position,
    sourceLocationId:f.a.id,confirmedSourceLocationCode:f.a.code,destinationLocationId:f.empty.id,
    confirmedDestinationLocationCode:f.empty.code,quantity:1,reason:'One unit moved before the next page',backing:[]})
  await page.keyboard.press('Enter')
  await expect(panel.getByRole('heading',{name:'Position timeline'})).toBeFocused()
  await expect(panel).toContainText('4 shown · Timeline page 2')
  await expect(rows.last()).toContainText('Put away')
  await expect(onHand(page)).toHaveText('5')
  await panel.getByRole('button',{name:'Newer events'}).focus()
  await page.keyboard.press('Enter')
  await expect(panel).toContainText('25 shown · Timeline page 1')
  await expect(rows.first()).toContainText('One unit moved before the next page')
})

test('undated facts retain their revision and a late response cannot populate a newly selected position', async ({page,context}) => {
  const f = await fixture(page)
  const originalPath = endpoint(f.position)
  const base: PositionTimelinePage = await (await page.request.get(originalPath)).json()
  expect(base.events.map(e=>e.kind)).toEqual(['offer-backing','putaway'])
  const firstRead = (u:URL) => u.pathname === originalPath && !u.searchParams.has('after')
  await page.route(firstRead,r=>r.fulfill({json:{...base,complete:false,
    events:[{...base.events[0],reason:'Unconfirmed event'}],position:{...base.position,layer:{...base.position.layer,quantity:999}}}}))
  await page.goto('/#inventory/'+f.position)
  const panel = timeline(page)
  await expect(panel).toContainText('Partial timeline read.')
  await expect(panel).not.toContainText('No events recorded')
  await expect(onHand(page)).toHaveCount(0)
  await expect(panel.getByRole('listitem')).toHaveCount(0)
  await expect(panel).not.toContainText('Unconfirmed event')
  await page.unroute(firstRead)
  const archived = {...base,hasUndatedEvents:true,events:base.events.map(e=>({...e,occurredAt:null}))}
  await page.route(firstRead,r=>r.fulfill({json:archived}))
  await panel.getByRole('button',{name:'Retry timeline page 1'}).click()
  await expect(panel).toContainText('Some events have no recorded time.')
  await expect(panel.getByRole('listitem').first()).toContainText('Revision 5 · Time not recorded')
  await expect(panel.getByRole('listitem').last()).toContainText('Revision 3 · Time not recorded')

  await page.unroute(firstRead)
  const next = await f.write('receiving/transfers',5,{ purchaseId:f.paid.purchaseId,sourcePlacementId:f.position,
    sourceLocationId:f.a.id,confirmedSourceLocationCode:f.a.code,destinationLocationId:f.empty.id,
    confirmedDestinationLocationCode:f.empty.code,quantity:1,reason:'One pouch to a different bin',backing:[] })
  let release!:()=>void
  const gate = new Promise<void>(resolve=>{release=resolve})
  await page.route(firstRead,async r=>{await gate;await r.fulfill({json:archived})},{times:1})
  const reading = page.waitForRequest(r=>firstRead(new URL(r.url())))
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await reading
  try {
    await page.evaluate(id=>{location.hash='inventory/'+id},next)
    await expect(panel.getByRole('listitem')).toHaveCount(1)
    await expect(panel).toContainText('Transferred in')
  } finally { release() }
  await context.setOffline(true)
  await expect(panel).toContainText('Showing the last confirmed timeline page.')
  await expect(panel).not.toContainText('Time not recorded')
  await expect(panel).not.toContainText('Offer backing added')
  await expect(onHand(page)).toHaveText('1')
  await context.setOffline(false)
  await expect(panel).toContainText('One pouch to a different bin')
  await expect(panel.getByText('Showing the last confirmed timeline page.',{exact:false})).toHaveCount(0)
})

test('a competing transfer between register selection and timeline read shows the paired zero balance and movement', async ({page}) => {
  const f = await fixture(page)
  await page.getByRole('link',{name:'Inventory'}).click()
  const query = page.getByRole('searchbox',{name:'Search stock positions'})
  await query.fill(f.paid.purchase)
  await query.press('Enter')
  const register = page.getByRole('region',{name:'Stock register'})
  const select = register.locator('[data-position="'+f.position+'"]')
  await expect(select.locator('xpath=ancestor::tr')).toContainText('6 on hand')

  const path = endpoint(f.position)
  let release!:()=>void
  const gate = new Promise<void>(resolve=>{release=resolve})
  await page.route(url=>url.pathname===path,async route=>{await gate;await route.continue()},{times:1})
  const reads: string[] = []
  page.on('request',r=>{
    const url = new URL(r.url())
    if (url.pathname===path || url.searchParams.get('positionId')===f.position) reads.push(url.pathname)
  })
  const first = page.waitForRequest(r=>new URL(r.url()).pathname===path)
  const result = page.waitForResponse(r=>new URL(r.url()).pathname===path)
  await select.click()
  await first
  try {
    await expect(inspector(page)).toContainText('Reading position balances and activity')
    await expect(onHand(page)).toHaveCount(0)
    await f.write('receiving/transfers',5,{purchaseId:f.paid.purchaseId,sourcePlacementId:f.position,sourceLocationId:f.a.id,
      confirmedSourceLocationCode:f.a.code,destinationLocationId:f.empty.id,confirmedDestinationLocationCode:f.empty.code,
      quantity:6,reason:'A second session moved the entire position',moveAllBacking:true,backing:[]})
  } finally {release()}
  const snapshot: PositionTimelinePage = await (await result).json()
  expect(snapshot.position.layer.quantity).toBe(0)
  expect(snapshot.events[0]).toMatchObject({kind:'transfer-out',physicalDelta:-6,offerBackedDelta:-3})
  await expect(onHand(page)).toHaveText('0')
  await expect(inspector(page)).toContainText('No units remain in this position.')
  await expect(timeline(page).getByRole('listitem').first()).toContainText('A second session moved the entire position')
  await expect(select.locator('xpath=ancestor::tr')).toContainText('6 on hand')
  expect(reads).toEqual([path])
})
