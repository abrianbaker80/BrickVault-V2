import { expect, type Page } from '@playwright/test'
import { audit, fixture, purchases } from './bin-fixture'
import { test } from './trusted'
import type { ReceivingWorkspace } from '../src/api'

type StockFixture = Awaited<ReturnType<typeof fixture>>
const path = '/api/v1/receiving/pooled-offers'
const save = (page: Page) => page.getByRole('button',{name:'Save unpublished offer'})
const expand = (page: Page) => page.getByRole('button',{name:'Include stock from other purchases'})

async function choose(page: Page, f: StockFixture) {
  await page.goto('/#receiving/' + f.paid.purchaseId)
  await page.getByRole('button',{name:'Prepare offer',exact:true}).click()
  await page.getByLabel('Offer item').selectOption(f.catalogId)
  await page.getByLabel('Manual unit price').fill('2.05')
  await page.getByLabel('Back units from ' + f.a.code + ' · ' + f.paid.delivery,{exact:true}).fill('1')
}

test('one draft preserves two purchase currencies and source positions after a lost confirmation', async ({page}, info) => {
  const f = await fixture(page)
  await choose(page,f)
  await expand(page).click()
  await page.getByLabel('Back units from ' + f.unknown.purchase + ' · ' + f.a.code + ' · ' + f.unknown.delivery).fill('2')
  await expect(page.getByText('3 units across 2 purchases · 1 from ' + f.paid.purchase)).toBeVisible()
  await page.setViewportSize({width:1440,height:900})
  await page.screenshot({path:info.outputPath('pooled-offer-desktop.png')})
  await page.setViewportSize({width:360,height:800})
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
  await page.screenshot({path:info.outputPath('pooled-offer-mobile.png')})
  await audit(page)

  let original = '', entity = '', correlation = ''
  await page.route('**' + path, async route => {
    original = route.request().postData()!
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    const receipt = await response.json()
    entity = receipt.entityId; correlation = receipt.correlationId
    await response.dispose()
    await route.abort('failed')
  }, {times:1})
  await save(page).click()
  const pending = page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText('Unpublished offer from multiple purchases')
  await page.reload()
  const replay = page.waitForResponse(r => new URL(r.url()).pathname === path && r.request().method() === 'POST')
  await pending.getByRole('button',{name:'Retry command'}).click()
  const response = await replay
  expect(response.request().postData()).toBe(original)
  expect(await response.json()).toMatchObject({kind:'offer',entityId:entity,correlationId:correlation})
  await expect(pending).toHaveCount(0)
  const body = JSON.parse(original)
  expect(body).toMatchObject({purchaseId:f.paid.purchaseId,expectedRevision:5,expectedCatalogRevision:1,currency:'USD',
    unitPriceMinor:205,backing:[{placementId:f.position,quantity:1}],
    additionalSources:[{purchaseId:f.unknown.purchaseId,expectedRevision:3,backing:[{quantity:2}]}]})
  const rows = await purchases(page,[f.paid.purchaseId,f.unknown.purchaseId])
  for (const source of [f.paid,f.unknown]) {
    const paid = source === f.paid, purchase = rows.find(p => p.id === source.purchaseId)!
    expect(purchase.currency).toBe(paid ? 'USD' : 'EUR')
    expect(purchase.deliveries[0].itemCostMinor).toBe(paid ? 1234 : null)
    expect(purchase.offers.find(o => o.id === entity)).toMatchObject({backedQuantity:paid ? 1 : 2,publishedQuantity:0,
      pool:{originPurchaseId:f.paid.purchaseId,totalBackedQuantity:3}})
    await page.goto('/#receiving/' + purchase.id)
    const contributions = page.getByRole('list',{name:'Contributing purchases'})
    await expect(contributions.getByRole('link',{name:f.paid.purchase,exact:true})).toBeVisible()
    await expect(contributions.getByRole('link',{name:f.unknown.purchase,exact:true})).toBeVisible()
    await expect(page.getByText((paid ? '1' : '2') + ' backed by this purchase')).toBeVisible()
    await expect(page.getByText('3 backed across 2 purchases')).toBeVisible()
  }
  await page.route('**/api/v1/receiving/current?**', async route => {
    const reply = await route.fetch()
    const snapshot:ReceivingWorkspace = await reply.json()
    const purchase = snapshot.purchases.find(p => p.id === f.paid.purchaseId)!
    const offer = purchase.offers.find(row => row.id === entity)!
    offer.pool!.sources[0].backedQuantity++
    await route.fulfill({response:reply,json:snapshot})
    await reply.dispose()
  }, {times:1})
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await expect(page.getByRole('alert')).toContainText('could not verify the server response')
})

test('a change to a selected contributor blocks the frozen offer until the operator reviews the source again', async ({page}) => {
  const f = await fixture(page)
  await choose(page,f)
  await expand(page).click()
  const extra = page.getByLabel('Back units from ' + f.unknown.purchase + ' · ' + f.a.code + ' · ' + f.unknown.delivery)
  await extra.fill('2')
  const [contributor] = await purchases(page,[f.unknown.purchaseId])
  await f.write('receiving/offers',contributor.revision,{purchaseId:contributor.id,catalogId:f.catalogId,
    unitPriceMinor:120,backing:[{placementId:contributor.deliveries[0].placements[0].id,quantity:1}]})
  await page.getByRole('button',{name:'Refresh',exact:true}).click()
  await expect(page.getByText('A contributing purchase changed.',{exact:false})).toBeVisible()
  await expect(save(page)).toBeDisabled()
  await page.getByRole('button',{name:'Review contributing purchases'}).click()
  await expand(page).click()
  await expect(extra).toBeEmpty()
  await expect(page.getByLabel('Manual unit price')).toHaveValue('2.05')
})

test('an older read without capability proof keeps ordinary drafts available', async ({page}) => {
  const f = await fixture(page)
  await page.route('**/api/v1/receiving/current?**', async route => {
    const response = await route.fetch(), pageBody = await response.json(), headers = {...response.headers()}
    delete headers['x-brickvault-pooled-offers']
    delete headers['x-brickvault-set-contents']
    delete headers['content-length']
    pageBody.purchases = pageBody.purchases.filter((p: {id:string}) => [f.paid.purchaseId,f.unknown.purchaseId].includes(p.id))
    await route.fulfill({status:response.status(),headers,json:pageBody})
    await response.dispose()
  })
  await choose(page,f)
  await expect(expand(page)).toHaveCount(0)
  await expect(save(page)).toBeEnabled()
  const body = { commandId:expect.any(String),expectedRevision:5,purchaseId:f.paid.purchaseId,catalogId:f.catalogId,
    backing:[{placementId:f.position,quantity:1}],unitPriceMinor:205 }
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/receiving/offers' && r.request().method() === 'POST')
  await save(page).click()
  const posted = await response
  expect(posted.request().postDataJSON()).toEqual(body)
})
