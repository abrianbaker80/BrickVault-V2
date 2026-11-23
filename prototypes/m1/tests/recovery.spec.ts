import { expect, test } from '@playwright/test'

for (const engine of ['react', 'vue'])
  test(engine + ' filter includes each contributing batch', async ({ page }) => {
    await page.goto('/?engine=' + engine)
    const batch = page.getByRole('combobox', { name: 'Filter batch' })
    for (const [id, count, includes] of [
      ['IN-024', 4, true],
      ['IN-026', 5, true],
      ['IN-027', 4, false]
    ] as const) {
      await batch.selectOption(id)
      await expect(page.locator('tbody tr')).toHaveCount(count)
      await expect(page.locator('[data-inspect="BV-1024"]')).toHaveCount(includes ? 1 : 0)
    }
    await page.getByRole('button', { name: 'Clear filters' }).click()
    await expect(page.locator('tbody tr')).toHaveCount(12)
  })

test('a reloaded order retains the reservation, picks and short count until reset', async ({ page }) => {
  await page.goto('/?view=orders')
  await page.getByRole('textbox', { name: 'Bag label', exact: true }).fill('BAG-41A')
  await page.getByRole('button', { name: 'Confirm bag' }).click()
  await page.getByRole('button', { name: 'Report short count' }).click()
  await page.reload()
  await expect(page.getByText('Packing blocked', { exact: true })).toBeVisible()
  await expect(page.getByText('2 of 4 units picked')).toBeVisible()
  await page.getByLabel('Recovered bag label').fill('BAG-41A')
  await page.getByRole('button', { name: 'Confirm recovered bag' }).click()
  await expect(page.getByRole('status')).toContainText('already')
  await page.getByRole('button', { name: 'Review guide' }).click()
  await page.getByRole('button', { name: 'Reset all demo records' }).click()
  await page.reload()
  await expect(page.getByText('0 of 4 units picked')).toBeVisible()
})

test('undo preserves receipt history and returns sellable units to their previous custody', async ({
  page
}) => {
  await page.goto('/?view=receiving')
  await page.getByLabel('Item code').fill('PL-3020')
  await page.getByLabel('Units received').fill('5')
  await page.getByRole('button', { name: 'Receive units' }).click()
  await page.getByLabel('Destination bin').fill('B-12')
  await page.getByRole('button', { name: 'Confirm putaway' }).click()
  const sellable = page
    .locator('dt')
    .filter({ hasText: /^Sellable$/ })
    .locator('xpath=following-sibling::dd')
  await expect(sellable).toHaveText('5')
  await page.getByText('Undo last receipt step', { exact: true }).click()
  await page.getByRole('button', { name: 'Reverse putaway' }).click()
  await expect(sellable).toHaveText('0')
  await expect(page.getByText('Putaway confirmed', { exact: true })).toBeVisible()
  await page.getByText('Undo last receipt step', { exact: true }).click()
  await page.getByRole('button', { name: 'Reverse receipt' }).click()
  await expect(
    page
      .locator('dt')
      .filter({ hasText: /^Still to receive$/ })
      .locator('xpath=following-sibling::dd')
  ).toHaveText('12')
})
