import { test as base, firefox } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { mkdtempDisposable } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

export function prepareProfile(profile: string) {
  const python = process.platform === 'win32' ? 'python' : 'python3'
  const helper = fileURLToPath(new URL('../../../scripts/foundation/browser.py', import.meta.url))
  execFileSync(python, [helper, 'prepare', '--profile', profile, '--firefox', firefox.executablePath(),
    '--ca', process.env.BV_LAB_CA!], { stdio: 'inherit' })
}

export const test = base.extend({
  browser: [async ({}, use) => {
    await using scratch = await mkdtempDisposable(join(tmpdir(), 'brickvault-qualified-'))
    const profile = join(scratch.path, 'profile')
    prepareProfile(profile)
    const context = await firefox.launchPersistentContext(profile)
    try { await use(context.browser()!) } finally { await context.close() }
  }, { scope: 'worker' }]
})
