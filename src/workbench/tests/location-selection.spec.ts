import {expect, type Route} from '@playwright/test'
import {test} from './trusted'
import {fixture, stock} from './bin-fixture'

function holdLastPage() {
  let arrived!: () => void, release!: () => void
  const pending = new Promise<void>(resolve => { arrived = resolve })
  const resume = new Promise<void>(resolve => { release = resolve })
  return { pending, release, async inspect(route: Route, response: Awaited<ReturnType<Route['fetch']>>) {
    if (!(await response.json() as { nextAfter: string | null }).nextAfter) { arrived(); await resume }
    await route.fulfill({ response })
  }}
}

for (const width of [1440, 320]) {
  test(`a capacity read keeps the intended bin under the pointer at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 })
    const f = await fixture(page)
    await f.revise()
    for (let i = 0; i < 18; i++) {
      const code = '0-' + f.a.code + '-' + i
      const id = await f.write('locations', 0, { code, name: 'Fictional overflow shelf for returned component trays ' + code })
      if (i === 0) await f.write('locations/capacities', 1, { locationId: id, pieceLimit: 9007199254740991, reason: 'Verify the displayed limit' })
    }
    await f.write('locations/capacities', 1, { locationId: f.a.id, pieceLimit: 3, reason: 'Check overflow' })
    let gate = holdLastPage()
    await page.route(u => u.pathname.endsWith('/locations/registry'), async route => {
      const check = gate, response = await route.fetch()
      try { await check.inspect(route, response) } finally { await response.dispose() }
    })
    await page.reload()
    const target = page.getByRole('button', { name: 'Inspect ' + f.a.name, exact: true })
    const row = page.getByRole('row').filter({ has: target })
    await gate.pending
    await target.hover()
    const bounds = await target.boundingBox()
    if (!bounds) throw new Error('The registered bin has no pointer target.')
    const pointer = { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 }
    gate.release()
    await expect(row).toContainText('above limit')
    // A pointer already aimed at this bin must stay on it when the complete capacity page arrives.
    const targetAtPointer = () => target.evaluate((el, p) => el.contains(document.elementFromPoint(p.x, p.y)), pointer)
    expect(await targetAtPointer()).toBe(true)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)

    await f.write('locations/archives', 2, { locationId: f.a.id, confirmedLabel: f.a.code,
      expectedPhysicalQuantity: 9, expectedPositionCount: 2, reason: 'Retire the bin with its stock intact' })
    gate = holdLastPage()
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
    await gate.pending
    await expect(row).toContainText('Last confirmed')
    expect(await targetAtPointer()).toBe(true)
    await page.mouse.down()
    gate.release()
    await expect(row).toContainText('Archived')
    expect(await targetAtPointer()).toBe(true)
    await page.mouse.up()
    await expect(page.getByRole('heading', { name: f.a.name, exact: true })).toBeVisible()
    await expect(stock(page).getByRole('listitem').filter({ hasText: f.unknown.purchase })).toContainText('Placed at catalog revision 1.')
  })
}
