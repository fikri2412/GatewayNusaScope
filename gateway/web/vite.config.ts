import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    strictPort: true,
    proxy: { '/admin/api': 'http://localhost:5090', '/platform/api': 'http://localhost:5090' },
  },
  // Hasil build dilayani langsung oleh AiGateway.Api.
  build: { outDir: '../src/AiGateway.Api/wwwroot', emptyOutDir: true },
})
