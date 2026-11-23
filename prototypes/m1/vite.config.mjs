import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  base: './',
  plugins: [react(), vue()],
  build: { manifest: true, target: 'es2022', assetsInlineLimit: 0 }
})
