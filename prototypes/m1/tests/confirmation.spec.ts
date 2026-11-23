import { test, expect, type Page } from '@playwright/test'

async function startOrder(page: Page, design: string) {
  await page.goto('/?view=orders&design=' + design)
  const bag = page.getByRole('textbox', { name: 'Bag label', exact: true })
  await bag.fill('BAG-41A')
  await page.getByRole('button', { name: 'Confirm bag' }).click()
  await bag.fill('BAG-41B')
  return bag
}

for (const design of ['workbench', 'ledger']) {
  test(design + ' held scan terminator cannot confirm packing or physical printing', async ({ page }) => {
    const bag = await startOrder(page, design)
    await bag.focus()
    try {
      for (let i = 0; i < 4; i++) await page.keyboard.down('Enter')
    } finally {
      await page.keyboard.up('Enter')
    }
    await expect(page.getByRole('button', { name: 'Confirm packed' })).toBeVisible()
    await expect(page.getByText('Parcel packed', { exact: true })).toHaveCount(0)
    await expect(page.getByText('Printed label verified', { exact: true })).toHaveCount(0)
  })
  test(design + ' a packing double-click issues exactly one command', async ({ page }) => {
    await startOrder(page, design)
    await page.getByRole('button', { name: 'Confirm bag' }).click()
    await page.getByRole('button', { name: 'Confirm packed' }).dblclick()
    await expect(page.getByRole('button', { name: 'Queue parcel label' })).toBeVisible()
    await expect(page.getByText('Label queued', { exact: true })).toHaveCount(0)
    await expect(page.getByText('Parcel packed', { exact: true })).toHaveCount(1)
  })
}

test('receipt errors and their descriptions follow the field that currently fails', async ({ page }) => {
  await page.goto('/?view=receiving')
  const item = page.getByLabel('Item code')
  const quantity = page.getByLabel('Units received')
  const send = page.getByRole('button', { name: 'Receive units' })
  await item.fill('wrong')
  await send.click()
  await expect(item).toHaveAttribute('aria-invalid', 'true')
  await item.fill('PL-3020')
  await quantity.fill('15')
  await send.click()
  await expect(quantity).toHaveAttribute('aria-invalid', 'true')
  await expect(quantity).toHaveAccessibleDescription(/whole quantity/)
  await expect(item).not.toHaveAttribute('aria-invalid', 'true')
  await expect(item).not.toHaveAccessibleDescription(/whole quantity/)
})
