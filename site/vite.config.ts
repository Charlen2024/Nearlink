import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  base: '/nearlink/',
  plugins: [vue()],
  build: {
    outDir: 'web',
    emptyOutDir: true,
    assetsDir: 'assets',
  },
})
