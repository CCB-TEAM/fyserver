import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

const root = fileURLToPath(new URL('.', import.meta.url));
const backend = process.env.FYSERVER_DEV_ORIGIN || 'http://127.0.0.1:1145';
const pages = Object.fromEntries(
  readdirSync(root)
    .filter((name) => name.endsWith('.html'))
    .map((name) => [name.slice(0, -5), fileURLToPath(new URL(name, import.meta.url))]),
);

export default defineConfig({
  plugins: [vue()],
  root,
  base: '/admin-ui/',
  publicDir: 'public',
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/admin/api': backend,
      '/admin-ui/uploads': backend,
    },
  },
  build: {
    outDir: '../wwwroot/admin-ui',
    emptyOutDir: false,
    assetsDir: 'assets',
    rollupOptions: {
      input: pages,
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
});
