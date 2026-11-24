import { expect, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { fixture } from './bin-fixture'
import { holdPositionLink } from '../src/countLinks'
import type { CountSnapshot } from '../src/api'

const dialog = (page: Page) => page.getByRole('dialog', { name: 'Build holds', exact: true })
const refresh = (page: Page) => page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))

async function heldPosition(page: Page) {
  const f = await fixture(page)
  const result = await page.request.get('/api/v1/receiving/bins/' + f.a.id + '/counts')
  expect(result.ok()).toBe(true)
  const position = ((await result.json()) as CountSnapshot).positions.find(p => p.purchaseId === f.unknown.purchaseId)!
  const base = { locationId: f.a.id, positionId: position.positionId, purchaseId: position.purchaseId, confirmedLocationCode: f.a.code }
  const holdId = await f.write('receiving/holds', position.purchaseRevision, { ...base, quantity: 2, purpose: 'Footbridge', reason: 'Keep the bridge pieces together' })
  const url = '/api/v1/receiving/bins/' + f.a.id + '/positions/' + position.positionId + '/holds'
  return { ...f, position, base, holdId, url, link: '/' + holdPositionLink(f.a.id, position.positionId) }
}

test('a newer witnessed count rejects the old release decision and shows the exact replacement evidence', async ({ page }) => {
  const f = await heldPosition(page)
  const rev = f.position.purchaseRevision
  const first = await f.write('receiving/counts',rev+1,{...f.base,countedQuantity:1,reason:'First witnessed tray count'})
  await page.goto(f.link)
  const hold = dialog(page)
  const evidence = hold.getByRole('region',{name:'Shortage evidence'})
  await expect(evidence).toContainText('1 observed · 3 in the ledger at count · 2 missing')
  await expect(evidence.getByRole('link')).toHaveAttribute('href', '#locations/' + f.a.id + '/counts/' + first)
  const choose = hold.getByRole('button',{name:'Release pieces from Footbridge'})
  await choose.click()
  await hold.getByLabel('Quantity to release').fill('1')
  await hold.getByLabel('Reason',{exact:true}).fill('Proposed using first count')
  let newer = ''
  await page.route('**/api/v1/receiving/holds/release',async route => {
    expect(route.request().postDataJSON().expectedObservationId).toBe(first)
    newer = await f.write('receiving/counts',rev+2,{...f.base,countedQuantity:2,reason:'Second tray count with owner'})
    const response = await route.fetch()
    expect(response.status()).toBe(409)
    expect((await response.json()).code).toBe('revision_conflict')
    await route.fulfill({response}); await response.dispose()
  }, {times:1})
  const confirm = hold.getByRole('button',{name:'Confirm release'})
  await confirm.click()
  await expect(hold).toContainText('Stock or count evidence changed.')
  await expect(confirm).toBeDisabled()
  await expect(hold).toContainText('This purchase has changed.')
  await expect(evidence).toContainText('2 observed · 3 in the ledger at count · 1 missing')
  await expect(evidence.getByRole('link')).toHaveAttribute('href', '#locations/' + f.a.id + '/counts/' + newer)
  expect(await page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('brickvault.v2.pending.')))).toEqual([])
  await hold.getByRole('button',{name:'Cancel'}).click()
  await expect(hold.getByRole('listitem')).toContainText('2 held · 0 released')
  await choose.click()
  await expect(hold.getByLabel('Quantity to release')).toHaveValue('')
  await hold.getByLabel('Quantity to release').fill('2')
  await hold.getByLabel('Reason',{exact:true}).fill('Review latest quantity')
  await f.write('receiving/holds/release',rev+3,{purchaseId:f.position.purchaseId,holdId:f.holdId,quantity:1,
    shortageId:first,expectedObservationId:newer,reason:'Owner selected one piece in a second session'})
  await refresh(page)
  await expect(hold).toContainText('Stock or count evidence changed.')
  await expect(confirm).toBeDisabled()
  await hold.getByRole('button',{name:'Cancel'}).click()
  await expect(hold.getByRole('listitem')).toContainText('1 held · 1 released')
})

test('partial, offline, and wrong-scope hold reads retain confirmed evidence and block decisions until retry', async ({page,context}) => {
  const f = await heldPosition(page)
  await f.write('receiving/holds/release',f.position.purchaseRevision+1,{purchaseId:f.position.purchaseId,holdId:f.holdId,quantity:1,reason:'Returned one piece to stock'})
  await page.setViewportSize({width:320,height:720})
  const scoped = (u:URL) => u.pathname === f.url
  await page.route(scoped,async route => {
    const result = await route.fetch()
    await route.fulfill({json:{...await result.json(),complete:false,holds:[]}})
    await result.dispose()
  },{times:1})
  await page.goto(f.link)
  const hold = dialog(page)
  await expect(hold).toContainText('Hold or count evidence is incomplete.')
  await expect(hold).not.toContainText('No build holds recorded')
  await expect(hold.getByRole('button',{name:'Create build hold'})).toBeDisabled()
  await hold.getByRole('button',{name:'Retry hold read'}).press('Enter')
  const project = hold.getByRole('listitem').filter({hasText:'Footbridge'})
  const release = project.getByRole('button',{name:'Release pieces from Footbridge'})
  await expect(release).toBeEnabled()
  const stamp = hold.getByText(/^Position read /)
  const previous = await stamp.textContent()
  await page.route(scoped,async route => {
    const result = await route.fetch()
    await route.fulfill({json:{...await result.json(),complete:false,holds:[]}})
    await result.dispose()
  },{times:1})
  await refresh(page)
  await expect(hold).toContainText('Partial hold read.')
  await expect(stamp).toHaveText(previous!)
  await expect(release).toBeDisabled()
  await page.route(scoped,async route=>{
    const result = await route.fetch()
    await route.fulfill({json:{...await result.json(),positionId:randomUUID(),holds:[]}})
    await result.dispose()
  },{times:1})
  await hold.getByRole('button',{name:'Retry hold read'}).click()
  await expect(hold).toContainText('could not verify the server response')
  await expect(stamp).toHaveText(previous!)
  await expect(release).toBeDisabled()
  await hold.getByRole('button',{name:'Retry hold read'}).click()
  await expect(release).toBeEnabled()
  await project.getByRole('button',{name:'Release history'}).click()
  const history = project.getByRole('region',{name:'Release history'})
  await expect(history).toContainText('Returned one piece to stock')
  const historyDate = history.getByText(/^Release read /)
  const savedDate = await historyDate.textContent()
  const historyPath = (u:URL) => u.pathname === f.url + '/' + f.holdId + '/releases'
  await page.route(historyPath,async route=>{
    const result = await route.fetch()
    await route.fulfill({json:{...await result.json(),complete:false,releases:[]}})
    await result.dispose()
  },{times:1})
  await refresh(page)
  await expect(history).toContainText('Partial release history.')
  await expect(historyDate).toHaveText(savedDate!)
  await expect(history).toContainText('Returned one piece to stock')
  await history.getByRole('button',{name:'Retry release history'}).click()
  await expect(history).not.toContainText('Partial release history.')
  await context.setOffline(true)
  await expect(release).toBeDisabled()
  await expect(history).toContainText('Showing the last release read.')
  await context.setOffline(false)
  await expect(release).toBeEnabled()
  await release.click()
  await page.keyboard.press('Escape')
  await expect(hold).toHaveCount(0)
  await expect(page.getByRole('heading',{name:'Stock in this bin'})).toBeVisible()
})

for (const kind of ['hold', 'release'] as const) {
  test('size reconciliation verifies every immutable input of the original ' + kind, async ({page}) => {
    const f = await heldPosition(page)
    await page.goto(f.link)
    const hold = dialog(page)
    const creating = kind === 'hold'
    await hold.getByRole('button',{name:creating ? 'Create build hold' : 'Release pieces from Footbridge'}).click()
    if (creating) {
      await hold.getByLabel('Build or project').fill('Remaining piece project')
      await hold.getByLabel('Confirm physical bin code').fill(f.a.code)
    }
    await hold.getByLabel(creating ? 'Quantity to reserve' : 'Quantity to release').fill('1')
    await hold.getByLabel('Reason',{exact:true}).fill('Owner reviewed the named project')
    let original = ''
    const post = '**/api/v1/receiving/holds' + (creating ? '' : '/release')
    await page.route(post,async route => {
      original = route.request().postData()!
      const response = await route.fetch()
      expect(response.ok()).toBe(true)
      await response.dispose()
      await route.abort('connectionreset')
    },{times:1})
    await hold.getByRole('button',{name:creating ? 'Confirm hold' : 'Confirm release'}).click()
    const retry = page.getByRole('button',{name:'Retry command'})
    await expect(retry).toBeVisible()
    await page.reload()
    const exactPath = f.url + (creating ? '' : '/' + f.holdId + '/releases')
    const exact = (u:URL) => u.pathname === exactPath && u.searchParams.get('commandId') === JSON.parse(original).commandId
    const originals = JSON.parse(original)
    const reads = creating ? 'holds' : 'releases'
    const altered = [{expectedPurchaseRevision:originals.expectedRevision+1},
      creating ? {confirmedLocationCode:'OTHER-BIN'} : {purchaseId:randomUUID()}]
    await page.route(post,async route=>{
      expect(route.request().postData()).toBe(original)
      const padded = original + ' '.repeat(16384)
      const response = await route.fetch({postData:padded,headers:{...route.request().headers(),'content-length':String(Buffer.byteLength(padded))}})
      expect(response.status()).toBe(413)
      await route.fulfill({response}); await response.dispose()
    },{times:altered.length+1})
    for (const changed of altered) {
      await page.route(exact,async route=>{
        const response = await route.fetch()
        const data = await response.json()
        expect(data[reads]).toHaveLength(1)
        await route.fulfill({json:{...data,[reads]:[{...data[reads][0],...changed}]}})
        await response.dispose()
      },{times:1})
      await retry.click()
      await expect(page.getByRole('alert')).toContainText('verify the original command')
      await expect(retry).toBeDisabled()
      await page.getByRole('button',{name:'Refresh',exact:true}).click()
      await expect(retry).toBeEnabled()
    }
    const checked = page.waitForResponse(r=>exact(new URL(r.url())))
    await retry.click()
    const receipt = (await (await checked).json())[reads][0]
    expect(receipt.commandId).toBe(originals.commandId)
    expect(receipt.expectedPurchaseRevision).toBe(originals.expectedRevision)
    expect(creating ? receipt.confirmedLocationCode : receipt.purchaseId).toBe(creating ? f.a.code : f.position.purchaseId)
    await expect(retry).toHaveCount(0)
    await expect(page.getByText(/^Confirmed the original /)).toBeVisible()
  })
}
