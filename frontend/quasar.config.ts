import { fileURLToPath } from 'node:url';
import { defineConfig } from '#q-app';

export default defineConfig(() => ({
  boot: ['pinia', 'theme', 'i18n', 'error-reporter', 'tanstack-query', 'axios'],
  css: ['themes/tokens.css', 'themes/workbench.css', 'themes/airy.css', 'themes/enterprise.css', 'themes/industrial.css', 'themes/glass.css', 'themes/bento.css', 'themes/command.css', 'themes/botanical.css', 'themes/quasar-bridge.css', 'themes/print.css', 'themes/brand.css'],
  extras: ['material-icons'],
  build: {
    target: { browser: ['es2022'], node: 'node20' },
    vueRouterMode: 'history' as const,
    alias: { src: fileURLToPath(new URL('./src', import.meta.url)), shared: fileURLToPath(new URL('../shared', import.meta.url)) },
    // 前端版本與後端同一組 build-arg（Dockerfile）；沒給是空字串，程式顯示 dev（src/services/ops/build-info.ts）
    defineEnv: {
      APP_VERSION: process.env.APP_VERSION ?? '',
      GIT_COMMIT: process.env.GIT_COMMIT || process.env.RENDER_GIT_COMMIT || '',
      BUILD_TIME: process.env.BUILD_TIME ?? '',
    },
    // source map 只留在建置產物給維運還原呼叫框，不寫 sourceMappingURL；Dockerfile 再把 .map 移出 wwwroot（設計 T9）
    extendViteConf(viteConf) {
      viteConf.build = { ...viteConf.build, sourcemap: 'hidden' };
    },
  },
  devServer: {
    port: 9180,
    open: false,
    proxy: { '/api': { target: 'http://127.0.0.1:5180', changeOrigin: true } },
  },
  framework: { config: {}, plugins: ['Notify', 'Dialog'] },
}));
