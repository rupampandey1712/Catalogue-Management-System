import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// API_TARGET=http://localhost:5000 routes the dev server through the YARP gateway instead of the API.
const target = process.env.API_TARGET ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target, changeOrigin: true }
    }
  }
})
