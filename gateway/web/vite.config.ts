import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    strictPort: true,
    proxy: { '/admin/api': 'https://localhost:44387', '/platform/api': 'https://localhost:44387' },
  },
  // Hasil build dilayani langsung oleh AiGateway.Api.
  build: { outDir: '../src/AiGateway.Api/wwwroot', emptyOutDir: true },
})
