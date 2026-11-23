import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { readFileSync } from 'node:fs'

export default defineConfig({
  plugins: [vue(), {
    name: 'inter-font-license',
    generateBundle() {
      // Vite collects notices from bundled modules. CSS references the font as a binary asset.
      this.emitFile({ type: 'asset', fileName: 'Inter-OFL.txt',
        source: readFileSync(new URL('./node_modules/@fontsource-variable/inter/LICENSE', import.meta.url), 'utf8') })
    }
  }],
  build: { assetsInlineLimit: 0, license: { fileName: 'THIRD-PARTY-NOTICES.txt' } }
})
