/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { reactRuntime } from './vite-plugins/reactRuntime.ts';
import { angularRuntime } from './vite-plugins/angularRuntime.ts';
import { sqlRunner } from './vite-plugins/sqlRunner.ts';

// Local development:
//   SPA     → http://localhost:5173        (proxies /api to the ASP.NET Core API on :5080)
//   Runner  → http://127.0.0.1:5173/runner.html
// "localhost" and "127.0.0.1" are different origins, which mirrors production, where runner.html
// is served from its own domain. The iframe sandbox gives it an opaque origin on top of that.
export default defineConfig({
  plugins: [react(), tailwindcss(), reactRuntime(), angularRuntime(), sqlRunner()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: false },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
  },
});
