import { randomUUID } from 'node:crypto'
import { expect, type Page } from '@playwright/test'
import { fixture } from './bin-fixture'
import { test } from './trusted'

const offers = '**/api/v1/receiving/offers'
const journal = (page: Page, key: string) => page.evaluate(k => localStorage.getItem(k), key)
const pending = (page: Page) => page.getByRole('region', { name: 'Pending confirmations' })

function gate() {
  let release!: () => void
  const wait = new Promise<void>(resolve => { release = resolve })
  return { wait, release }
}

for (const attempt of ['first attempt', 'retry', 'legacy re-entry']) {
  for (const scope of ['issuing workspace', 'replacement workspace']) {
    for (const timing of ['during refresh', 'after refresh']) {
      test(attempt + ' rejected ' + timing + ' in the ' + scope, async ({ page }) => {
        const f = await fixture(page), issuer = f.headers['X-Workspace-Id']
        const other = randomUUID(), correlation = randomUUID()
        const active = scope === 'issuing workspace' ? issuer : other
        await page.goto('/#receiving/' + f.paid.purchaseId)
        await page.getByRole('button', { name: 'Prepare offer', exact: true }).click()
        await page.getByLabel('Offer item').selectOption(f.catalogId)
        await page.getByLabel('Selling currency').fill('JPY')
        await page.getByLabel('Manual unit price').fill('225')
        await page.getByLabel('Back units from ' + f.a.code + ' · ' + f.paid.delivery).fill('1')
        const save = page.getByRole('button', { name: 'Save unpublished offer' })
        if (attempt === 'legacy re-entry') {
          // An older Workbench recorded this command before selling currency was explicit.
          const purchaseId = await f.write('receiving/purchases', 0, { reference: 'OLD-' + f.a.code, source: 'Fictional supplier',
            currency: 'HUF', orderedQuantity: 1, pendingChargeMinor: null })
          const deliveryId = await f.write('receiving/deliveries', 1, { purchaseId, reference: 'Original pack',
            catalogId: f.catalogId, quantity: 1, itemCostMinor: null })
          const placementId = await f.write('receiving/putaway', 2, { purchaseId, deliveryId, catalogId: f.catalogId,
            locationId: f.a.id, confirmedLocationCode: f.a.code, quantity: 1 })
          const body = { commandId: randomUUID(), purchaseId, expectedRevision: 3,
            catalogId: f.catalogId, unitPriceMinor: 10, backing: [{ placementId, quantity: 1 }] }
          await page.evaluate(({ issuer, body }) => {
            const path = ['brickvault.v2.pending', issuer, body.commandId].join('.')
            localStorage.setItem(path, JSON.stringify({ kind: 'offer', workspaceId: issuer, body }))
          }, { issuer, body })
          await page.reload()
        } else if (attempt === 'retry') {
          await page.route(offers, r => r.abort('connectionreset'), { times: 1 })
          await save.click()
          await expect(pending(page)).toBeVisible()
          await page.reload()
        }
        const reject = gate(), auth = gate(), checkingSession = gate(), writing = gate()
        const signed = await (await page.request.get('/api/v1/session')).json()
        let authenticatedId = active
        const sent = page.waitForRequest(r => r.method() === 'POST' && r.url().endsWith('/receiving/offers'))
        await page.route('**/api/v1/**', async route => {
          const r = route.request()
          if (r.method() === 'POST' && r.url().endsWith('/receiving/offers')) {
            writing.release()
            await reject.wait
            await route.fulfill({ status: attempt === 'legacy re-entry' ? 409 : 400, json: {
              code: attempt === 'legacy re-entry' ? 'offer_price_reentry_required' : 'invalid_json',
              message: 'Check the original price.', correlationId: correlation
            } })
          } else if (r.method() === 'GET' && r.url().endsWith('/session')) {
            checkingSession.release()
            await auth.wait
            await route.fulfill({ json: { ...signed, workspaceId: authenticatedId } })
          } else await route.continue()
        })
        try {
          await (attempt === 'first attempt' ? save : pending(page).getByRole('button', { name: 'Retry command' })).click()
          await writing.wait
          const request = await sent, id = request.postDataJSON().commandId
          const issuerStoragePath = 'brickvault.v2.pending.' + issuer + '.' + id
          const otherStoragePath = 'brickvault.v2.pending.' + other + '.' + id
          const original = await journal(page, issuerStoragePath)
          expect(JSON.parse(original!).body.commandId).toBe(id)
          expect(JSON.parse(original!).body.currency).toBe(attempt === 'legacy re-entry' ? undefined : 'JPY')
          // The same ID in another workspace identifies an independent journal entry.
          const otherEntry = JSON.stringify({ workspaceId: other, kind: 'location', body: {
            commandId: id, expectedRevision: 0, code: 'SPARE', name: 'Shelf in other workspace'
          } })
          await page.evaluate(([key, value]) => localStorage.setItem(key, value), [otherStoragePath, otherEntry])
          const directory = page.waitForResponse(r => new URL(r.url()).searchParams.get('audit') === 'all')
          await page.getByRole('link', { name: 'Activity', exact: true }).click()
          await checkingSession.wait
          await expect(page.locator('.read-state')).toHaveText('Loading')
          if (timing === 'after refresh') {
            auth.release()
            await directory
            await expect(page.getByText('Connected', { exact: true })).toBeVisible()
          }
          const rejected = page.waitForResponse(r => r.request() === request)
          reject.release()
          await rejected
          if (timing === 'during refresh') {
            if (attempt !== 'retry') await expect.poll(() => journal(page, issuerStoragePath)).toBeNull()
            await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
            await expect(page.getByRole('alert').filter({ hasText: correlation })).toHaveCount(0)
            await expect(page.locator('.read-bar button')).toBeDisabled()
            auth.release()
            await directory
          }
          // The command handler has finished and the current workspace's state is visible.
          await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled()
          expect(await journal(page, issuerStoragePath)).toBe(attempt === 'retry' ? original : null)
          expect(await journal(page, otherStoragePath)).toBe(otherEntry)
          const alert = page.getByRole('alert')
          if (active === issuer) {
            await expect(alert).toContainText(correlation)
            await expect(alert).toContainText(attempt === 'legacy re-entry' ? 'Verify the original intended unit price' : 'JPY')
            await expect(page.getByText(attempt === 'retry' ? 'Disconnected' : 'Connected', { exact: true })).toBeVisible()
            if (attempt === 'retry') await expect(pending(page)).toContainText(id)
            if (attempt === 'first attempt' && timing === 'after refresh') {
              authenticatedId = other
              const replaced = page.waitForResponse(r => new URL(r.url()).searchParams.get('audit') === 'all')
              await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
              await replaced
              await expect(pending(page)).toContainText('Shelf in other workspace')
              await expect(alert).toHaveCount(0)
            }
          } else {
            await expect(alert).toHaveCount(0)
            await expect(page.getByText('Connected', { exact: true })).toBeVisible()
            await expect(pending(page)).toContainText('Shelf in other workspace')
          }
        } finally {
          reject.release()
          auth.release()
        }
      })
    }
  }
}
