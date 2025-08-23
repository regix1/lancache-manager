import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss()
  ],
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true
      },
      '/downloadHub': {
        target: 'http://localhost:5000',
        ws: true,
        changeOrigin: true
      }
    }
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true
  }
})