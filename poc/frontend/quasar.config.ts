import { fileURLToPath } from 'node:url';
import { defineConfig } from '@quasar/app-vite/wrappers';

export default defineConfig(() => ({
  boot: ['pinia', 'theme', 'i18n', 'tanstack-query', 'axios'],
  css: [],
  extras: ['material-icons'],
  build: {
    target: { browser: ['es2022'], node: 'node20' },
    vueRouterMode: 'history' as const,
    alias: { src: fileURLToPath(new URL('./src', import.meta.url)) },
  },
  devServer: {
    port: 9180,
    open: false,
    proxy: { '/api': { target: 'http://127.0.0.1:5180', changeOrigin: true } },
  },
  framework: { config: {}, plugins: ['Notify', 'Dialog'] },
}));
