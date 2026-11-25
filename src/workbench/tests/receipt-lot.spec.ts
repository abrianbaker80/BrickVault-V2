import { expect } from '@playwright/test'
import { test } from './trusted'
import { fixture } from './bin-fixture'
import type { ReceiptLot } from '../src/api'

test('a position opens its whole receipt lot and transfer ends remain inspectable on a narrow screen', async ({page}) => {
  const f = await fixture(page)
  const destination = await f.write('receiving/transfers', 5, {
    purchaseId: f.paid.purchaseId, sourcePlacementId: f.position, sourceLocationId: f.a.id,
    confirmedSourceLocationCode: f.a.code, destinationLocationId: f.empty.id,
    confirmedDestinationLocationCode: f.empty.code, quantity: 2, reason: 'Private lot fixture transfer',
    backing: [{offerId:f.firstOffer,quantity:1}]
  })
  await page.setViewportSize({width:420,height:820})
  await f.revise()
  await page.goto('/#inventory/' + f.position)
  await page.getByRole('link', {name:'Inspect receipt lot'}).click()
  const lot = page.getByRole('dialog', {name:'Receipt lot inspector'})
  await expect(lot.getByRole('heading', {name:f.paid.delivery})).toBeVisible()
  await expect(lot.getByText('USD 12.34', {exact:true})).toHaveCount(1)
  await expect(lot.getByText('In positions', {exact:true}).locator('+ dd')).toHaveText('8')
  await expect(lot.getByRole('region', {name:'Receipt lot positions'}).getByRole('listitem')).toHaveCount(3)
  const positions = lot.getByRole('region', {name:'Receipt lot positions'})
  await expect(positions.getByText('Recorded at catalog revision 1. Item details show current revision 2.')).toHaveCount(3)
  await expect(positions.getByText(/Reidentified plate/)).toHaveCount(3)
  await expect(lot.getByText('Transfer within lot', {exact:true})).toHaveCount(1)
  await expect(lot.getByText('2 moved from ' + f.a.code + ' to ' + f.empty.code + ' · 1 backed')).toBeVisible()
  await expect(lot.getByRole('link', {name:'Inspect source position'})).toHaveAttribute('href', '#inventory/' + f.position)
  await page.reload()
  await expect(lot.getByRole('link', {name:'Return to opening position'})).toHaveAttribute('href', '#inventory/' + f.position)
  await lot.getByRole('link', {name:'Inspect destination position'}).click()
  await expect(page).toHaveURL(new RegExp(destination + '$'))
  await expect(page.getByRole('link', {name:'Return to receipt lot'})).toBeVisible()
  await page.goBack()
  await expect(lot.getByRole('heading', {name:f.paid.delivery})).toBeVisible()
  expect(await lot.evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth + 1)).toBe(true)
})

test('independent lot cursors retain last confirmed data on partial, wrong-scope and stale reads', async ({page}) => {
  const f = await fixture(page)
  const path = '/api/v1/inventory/receipt-lots/' + f.paid.deliveryId
  let failure: 'partial' | 'scope' | '' = ''
  await page.route('**' + path + '?*', async route => {
    const url = new URL(route.request().url())
    url.searchParams.set('positionLimit','1')
    url.searchParams.set('eventLimit','1')
    const response = await route.fetch({url:url.toString()})
    if (!response.ok() || !failure) return route.fulfill({response})
    const data: ReceiptLot = await response.json()
    return route.fulfill({response,json:failure === 'partial' ? {...data,complete:false} : {...data,deliveryId:f.unknown.deliveryId}})
  })
  await page.goto('/#inventory/lot/' + f.paid.deliveryId)
  const positions = page.getByRole('region', {name:'Receipt lot positions'})
  const events = page.getByRole('region', {name:'Receipt lot activity'})
  await expect(positions.getByText('1 of 2 positions · Page 1')).toBeVisible()
  await expect(events.getByText('1 events shown · Page 1')).toBeVisible()
  await positions.getByRole('button', {name:'Next positions'}).click()
  await expect(positions.getByText('1 of 2 positions · Page 2')).toBeVisible()
  await expect(events.getByText('1 events shown · Page 1')).toBeVisible()
  failure = 'partial'
  await events.getByRole('button', {name:'Older lot events'}).click()
  await expect(page.getByText(/Partial receipt lot read.*last confirmed receipt lot read/)).toBeVisible()
  await expect(events.getByText('1 events shown · Page 1')).toBeVisible()
  failure = 'scope'
  await page.getByRole('button', {name:'Retry receipt lot read'}).click()
  await expect(page.getByText(/last confirmed receipt lot read/)).toBeVisible()
  await expect(positions.getByText('1 of 2 positions · Page 2')).toBeVisible()
  failure = ''
  await f.write('receiving/holds', 5, {
    purchaseId:f.paid.purchaseId, positionId:f.position, locationId:f.a.id, confirmedLocationCode:f.a.code,
    quantity:1,purpose:'Protect the fictional build',reason:'Separate committed hold'
  })
  await page.getByRole('button', {name:'Retry receipt lot read'}).click()
  await expect(page.getByRole('button', {name:'Reload lot from first pages'})).toBeVisible()
  await expect(positions.getByText('1 of 2 positions · Page 2')).toBeVisible()
  await page.getByRole('button', {name:'Reload lot from first pages'}).click()
  await expect(positions.getByText('1 of 2 positions · Page 1')).toBeVisible()
  await expect(events.getByText('Build hold placed')).toBeVisible()
})
