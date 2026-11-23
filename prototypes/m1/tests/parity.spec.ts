import { test, expect } from '@playwright/test'

for (const design of ['workbench', 'ledger']) {
  test(
    design + ' has equivalent first render, row order and semantics in React and Vue',
    async ({ page }) => {
      const captures: { screen: Buffer; semantics: string }[] = []
      for (const engine of ['react', 'vue']) {
        await page.goto('/?design=' + design + '&engine=' + engine)
        const workspace = page.locator('#module')
        await expect(workspace.getByRole('table').getByRole('row')).toHaveCount(13)
        await page.evaluate(async () => {
          await document.fonts.ready
          await Promise.all([...document.images].map((image) => image.decode()))
        })
        captures.push({ screen: await workspace.screenshot(), semantics: await workspace.ariaSnapshot() })
      }
      expect(captures[1].semantics).toBe(captures[0].semantics)
      // Match the rendered workspace while allowing subpixel glyph rasterization.
      const difference = await page.evaluate(
        async ([first, second]) => {
          const pixels = async (base64: string) => {
            const image = new Image()
            image.src = 'data:image/png;base64,' + base64
            await image.decode()
            const canvas = document.createElement('canvas')
            canvas.width = image.width
            canvas.height = image.height
            const context = canvas.getContext('2d')!
            context.drawImage(image, 0, 0)
            return context.getImageData(0, 0, canvas.width, canvas.height)
          }
          const a = await pixels(first)
          const b = await pixels(second)
          if (a.width !== b.width || a.height !== b.height) return 1
          let changed = 0
          for (let i = 0; i < a.data.length; i += 4) {
            if ([0, 1, 2].some((channel) => Math.abs(a.data[i + channel] - b.data[i + channel]) > 12))
              changed++
          }
          return changed / (a.width * a.height)
        },
        captures.map((c) => c.screen.toString('base64'))
      )
      expect(difference).toBeLessThan(0.001)
    }
  )
}
