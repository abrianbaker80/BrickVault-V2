import { expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { fixture } from './bin-fixture'
import type { Page } from '@playwright/test'

async function prepareOffer(page: Page) {
  const f = await fixture(page)
  await page.goto('/#receiving/'+f.paid.purchaseId)
  await page.getByRole('button',{name:'Prepare offer',exact:true}).click()
  await page.getByLabel('Offer item').selectOption(f.catalogId)
  await page.getByLabel('Manual unit price').fill('2.05')
  await page.getByLabel('Back units from '+f.a.code+' · '+f.paid.delivery,{exact:true}).fill('1')
  await page.getByRole('button',{name:'Include stock from other purchases'}).click()
  await page.getByLabel('Back units from '+f.unknown.purchase+' · '+f.a.code+' · '+f.unknown.delivery).fill('2')
  return f
}

test('a size-limited retry proves the original positions and quantities after a withdrawal', async ({page}) => {
  const f = await prepareOffer(page)
  const endpoint = '**/api/v1/receiving/pooled-offers'
  let offerId = '', commandId = '', correlation = ''
  await page.route(endpoint, async route => {
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    const receipt = await response.json()
    offerId = receipt.entityId; commandId = receipt.commandId; correlation = receipt.correlationId
    await response.dispose()
    await route.abort('connectionreset')
  }, {times:1})
  await page.getByRole('button',{name:'Save unpublished offer'}).click()
  const pending = page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText('Unpublished offer from multiple purchases')
  await f.write('inventory/draft-backing-withdrawals',6,{purchaseId:f.paid.purchaseId, positionId:f.position,
    offerId,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:1,expectedTotalOfferBackedQuantity:3,
    reason:'Remove this purchase from the draft'})
  const original = await page.evaluate(id => {
    const key = Object.keys(localStorage).find(k => k.endsWith('.'+id))!
    const raw = localStorage.getItem(key)!
    const changed = JSON.parse(raw)
    changed.body.additionalSources[0].backing[0].quantity = 1
    localStorage.setItem(key, JSON.stringify(changed))
    return {key,raw}
  }, commandId)
  const url = '/api/v1/receiving/pooled-offers/' + commandId + '/confirmation'
  await page.route(endpoint, route => route.fulfill({status:413,contentType:'application/json',
    json:{code:'request_too_large',message:'The gateway declined this retry.',correlationId:randomUUID()}}))
  await page.reload()
  const mismatch = page.waitForResponse(r => new URL(r.url()).pathname === url)
  await pending.getByRole('button',{name:'Retry command'}).click()
  expect((await mismatch).status()).toBe(409)
  await expect(pending).toBeVisible()
  expect(await page.evaluate(key => localStorage.getItem(key) !== null, original.key)).toBe(true)
  await page.evaluate(({key,raw}) => localStorage.setItem(key,raw), original)
  await page.reload()
  const exact = page.waitForResponse(r => new URL(r.url()).pathname === url)
  await pending.getByRole('button',{name:'Retry command'}).click()
  const proof = await exact
  expect(proof.status()).toBe(200)
  expect(await proof.json()).toMatchObject({kind:'offer',commandId,entityId:offerId,correlationId:correlation,revision:6})
  await expect(pending).toHaveCount(0)
})

test('a size-limited retry closes an unapplied offer before allowing a replacement', async ({page}) => {
  const f = await prepareOffer(page)
  const endpoint = '/api/v1/receiving/pooled-offers'
  await page.route('**'+endpoint, route => route.abort('connectionreset'), {times:1})
  const [original] = await Promise.all([
    page.waitForRequest(req => new URL(req.url()).pathname === endpoint),
    page.getByRole('button',{name:'Save unpublished offer'}).click()
  ])
  const body = original.postDataJSON()
  const url = endpoint + '/' + body.commandId + '/confirmation'
  const pending = page.getByRole('region',{name:'Pending confirmations'})
  await expect(pending).toContainText('Unpublished offer from multiple purchases')
  await page.route('**'+endpoint, route => route.fulfill({status:413,contentType:'application/json',
    json:{code:'request_too_large',message:'The gateway declined this retry.',correlationId:randomUUID()}}))
  await page.reload()
  const [proof] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === url),
    pending.getByRole('button',{name:'Retry command'}).click()
  ])
  expect(proof.status()).toBe(204)
  const digest = await proof.headerValue('X-BrickVault-Settled-Digest')
  expect(digest).toMatch(/^[a-f0-9]{64}$/)
  await expect(pending).toHaveCount(0)
  const headers = {...f.headers, 'X-BrickVault-Command-Digest':digest!}
  expect((await page.request.post(url,{headers})).status()).toBe(204)
  const mismatch = await page.request.post(url,{headers:{...headers,'X-BrickVault-Command-Digest':'0'.repeat(64)}})
  expect(mismatch.status()).toBe(409)
  expect(await mismatch.json()).toMatchObject({code:'command_mismatch'})
  const late = await page.request.post(endpoint,{headers:f.headers,data:body})
  expect(late.status()).toBe(409)
  expect(await late.json()).toMatchObject({code:'pooled_offer_unapplied'})
  await page.reload()
  await expect(page.getByRole('button',{name:'Prepare offer',exact:true})).toBeEnabled()
})
