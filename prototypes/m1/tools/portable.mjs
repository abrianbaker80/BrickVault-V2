import { build } from 'vite'
import react from '@vitejs/plugin-react'
import vue from '@vitejs/plugin-vue'
import { readFile, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../', import.meta.url))
const result = await build({
  configFile: false,
  root,
  base: './',
  plugins: [react(), vue()],
  build: {
    write: false,
    lib: { entry: 'src/main.ts', name: 'BrickVault', formats: ['iife'] },
    cssCodeSplit: false,
    rolldownOptions: { output: { codeSplitting: false } }
  },
  define: { 'process.env.NODE_ENV': JSON.stringify('production') }
})
const files = (Array.isArray(result) ? result[0] : result).output
const js = files.find((chunk) => chunk.type === 'chunk' && chunk.isEntry)
const css = files.find((chunk) => chunk.fileName.endsWith('.css'))
if (!js || !css || files.length !== 2)
  throw Error('The offline preview requires exactly one JavaScript bundle and one stylesheet.')
const template = await readFile(new URL('../index.html', import.meta.url), 'utf8')
const runtimePackages = [
  '@fontsource-variable/inter',
  '@fontsource/dm-serif-text',
  'lucide-static',
  'react',
  'react-dom',
  'scheduler',
  'vue',
  '@vue/shared',
  '@vue/reactivity',
  '@vue/runtime-core',
  '@vue/runtime-dom'
]
const notices = await Promise.all(
  runtimePackages.map(async (name) => {
    const license = await readFile(new URL('../node_modules/' + name + '/LICENSE', import.meta.url), 'utf8')
    return name + '\n\n' + license.trim()
  })
)
const licenses = notices.join('\n\n')
await writeFile(new URL('../dist/THIRD-PARTY.txt', import.meta.url), licenses + '\n')
const doc = template
  .replace(
    '</body>',
    () => '<script type="text/plain" id="third-party-notices">' + licenses + '</script></body>'
  )
  .replace('</head>', () => '<style>' + css.source + '</style></head>')
  .replace(
    /<script type="module"[^>]*><\/script>/,
    () => '<script>' + js.code.replace(/<\/script/gi, '<\\/script') + '</script>'
  )
await writeFile(new URL('../dist/portable.html', import.meta.url), doc)
console.log('Created standalone dist/portable.html')
