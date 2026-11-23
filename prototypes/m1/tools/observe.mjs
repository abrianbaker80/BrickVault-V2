import { chromium } from 'playwright'
import { mkdir, writeFile, readFile, mkdtemp, rm } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { gzipSync } from 'node:zlib'

const origin = process.argv[2] ?? 'http://127.0.0.1:49310'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Use a local preview.')
const evidence = resolve('evidence')
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch()
const results = []
const metrics = {}
async function open(page, design, engine, view = 'inventory') {
  await page.goto(origin + '/?' + new URLSearchParams({ design, engine, view }))
  await page
    .getByRole(view === 'inventory' ? 'table' : 'heading', {
      name: view === 'inventory' ? 'Inventory lots' : 'Blue plate delivery'
    })
    .waitFor()
  await page.evaluate(async () => {
    await document.fonts.ready
    await Promise.all([...document.images].map((i) => i.decode()))
  })
}
for (const width of [1440, 1280, 900]) {
  for (const design of ['workbench', 'ledger']) {
    const page = await browser.newPage({
      viewport: { width, height: width === 1440 ? 900 : width === 1280 ? 720 : 760 }
    })
    await open(page, design, 'react')
    await page.screenshot({ path: join(evidence, design + '-' + width + '.png') })
    results.push(
      await page.evaluate(
        ({ design, width }) => ({
          design,
          width,
          documentWidth: document.documentElement.scrollWidth,
          visibleLots: [...document.querySelectorAll('tbody tr')].filter(
            (r) => r.getBoundingClientRect().bottom <= innerHeight
          ).length
        }),
        { design, width }
      )
    )
    if (width === 900) {
      await page.getByRole('button', { name: /Inspect .*BV-1024/ }).click()
      await page.getByRole('dialog', { name: 'Lot inspector' }).waitFor()
      await page.screenshot({ path: join(evidence, design + '-inspector-900.png') })
    }
    if (width === 1280) {
      await open(page, design, 'react', 'receiving')
      await page.screenshot({ path: join(evidence, design + '-receiving.png') })
    }
    await page.close()
  }
}
const exceptions = await browser.newPage({ viewport: { width: 1280, height: 720 } })
await open(exceptions, 'workbench', 'react')
await exceptions.getByLabel('Read simulation').selectOption('Partial')
await exceptions.getByRole('group', { name: 'Partial inventory subtotal · 4 verified lots' }).waitFor()
await exceptions.evaluate(() => scrollTo(0, 0))
await exceptions.screenshot({ path: join(evidence, 'partial-stock.png') })
await exceptions.goto(origin + '/?design=ledger&view=recovery')
await exceptions.getByRole('button', { name: 'Read observations' }).click()
await exceptions.getByRole('button', { name: 'Review mismatch' }).click()
await exceptions.getByRole('dialog').waitFor()
await exceptions.evaluate(() => document.fonts.ready)
await exceptions.screenshot({ path: join(evidence, 'recovery-preview.png') })
await exceptions.close()

const samples = { react: [], vue: [] }
for (let run = 0; run < 9; run++) {
  for (const engine of run % 2 ? ['vue', 'react'] : ['react', 'vue']) {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
    await page.addInitScript(() => {
      const observer = new MutationObserver(() => {
        if (document.querySelector('[data-inspect="BV-1024"]')) {
          observer.disconnect()
          requestAnimationFrame(() =>
            requestAnimationFrame(() => {
              window.registerPaint = performance.now()
            })
          )
        }
      })
      observer.observe(document, { childList: true, subtree: true })
    })
    await open(page, 'workbench', engine)
    await page.waitForFunction(() => Number.isFinite(window.registerPaint))
    const latency = await page.evaluate(
      () =>
        new Promise((resolve) => {
          const start = performance.now()
          const field = document.querySelector('input[type=search]')
          const observer = new MutationObserver(() => {
            if (
              document.querySelectorAll('tbody tr').length === 1 &&
              document.querySelector('[data-inspect="BV-1027"]')
            ) {
              observer.disconnect()
              requestAnimationFrame(() => requestAnimationFrame(() => resolve(performance.now() - start)))
            }
          })
          observer.observe(document.querySelector('tbody'), { childList: true, subtree: true })
          Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(field, 'BV-1027')
          field.dispatchEvent(new Event('input', { bubbles: true }))
        })
    )
    samples[engine].push([await page.evaluate(() => window.registerPaint), latency])
    await page.close()
  }
}
const manifest = JSON.parse(await readFile('dist/.vite/manifest.json', 'utf8'))
for (const engine of ['react', 'vue']) {
  const chunk = Object.entries(manifest).find(([id]) => id.startsWith('src/inventory/' + engine + '.'))[1]
  const bytes = gzipSync(await readFile(join('dist', chunk.file))).length
  metrics[engine] = { gzipBytes: bytes, runs: samples[engine].map((pair) => pair.map(Math.round)) }
}
await browser.close()

// Chromium's tabs API sets real browser zoom. Keep the extension and profile ephemeral.
const scratch = await mkdtemp(join(tmpdir(), 'brickvault-zoom-'))
try {
  const ext = join(scratch, 'extension')
  await mkdir(ext)
  await writeFile(
    join(ext, 'manifest.json'),
    JSON.stringify({
      manifest_version: 3,
      name: 'Local preview zoom',
      version: '1.0',
      permissions: ['tabs'],
      background: { service_worker: 'worker.js' }
    })
  )
  await writeFile(join(ext, 'worker.js'), 'chrome.runtime.onInstalled.addListener(() => {});')
  const context = await chromium.launchPersistentContext(join(scratch, 'profile'), {
    channel: 'chromium',
    headless: true,
    viewport: null,
    args: ['--window-size=1280,720', '--disable-extensions-except=' + ext, '--load-extension=' + ext]
  })
  try {
    const worker = context.serviceWorkers()[0] ?? (await context.waitForEvent('serviceworker'))
    const page = await context.newPage()
    for (const design of ['workbench', 'ledger'])
      for (const engine of ['react', 'vue']) {
        await open(page, design, engine)
        const actual = await worker.evaluate(async (url) => {
          const tab = (await chrome.tabs.query({})).find((tab) => tab.url?.startsWith(url))
          await chrome.tabs.setZoom(tab.id, 1.5)
          return chrome.tabs.getZoom(tab.id)
        }, origin)
        await page.waitForFunction(() => devicePixelRatio >= 1.49 && innerWidth < 930)
        await page.getByRole('button', { name: /Inspect .*BV-1024/ }).click()
        await page.getByRole('dialog', { name: 'Lot inspector' }).waitFor()
        await page.keyboard.press('Escape')
        if (
          !(await page
            .getByRole('button', { name: /Inspect .*BV-1024/ })
            .evaluate((b) => b === document.activeElement))
        )
          throw Error('Inspector focus was lost at native zoom')
        await page.evaluate(() => window.scrollTo(0, 0))
        const geometry = await page.evaluate(() => ({
          width: innerWidth,
          height: innerHeight,
          documentWidth: document.documentElement.scrollWidth
        }))
        if (geometry.documentWidth > geometry.width) throw Error('Horizontal page overflow at 150% zoom')
        if (engine === 'vue') {
          const session = await context.newCDPSession(page)
          try {
            const capture = await session.send('Page.captureScreenshot', {
              format: 'png',
              fromSurface: true,
              captureBeyondViewport: false
            })
            const png = Buffer.from(capture.data, 'base64')
            const surfaceWidth = png.readUInt32BE(16)
            if (Math.abs(surfaceWidth - geometry.width * actual) > 3) {
              throw Error('Native capture did not include the full zoomed viewport: ' + surfaceWidth)
            }
            await writeFile(join(evidence, design + '-150pct.png'), png)
          } finally {
            await session.detach()
          }
        }
        results.push({ design, engine, zoom: actual, ...geometry })
      }
  } finally {
    await context.close()
  }
} finally {
  await rm(scratch, { recursive: true, force: true })
}
const record = { browser: 'Chromium ' + browser.version(), metrics, layouts: results }
await writeFile(join(evidence, 'measurements.json'), JSON.stringify(record, null, 2) + '\n')
console.log(JSON.stringify(record))
