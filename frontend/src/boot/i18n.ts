import { defineBoot } from '#q-app';
import { i18n } from 'src/i18n/instance';
import { startLocale } from 'src/i18n/startup';

export { i18n };

export default defineBoot(async ({ app }) => {
  app.use(i18n);
  // 有瀏覽器快取時不等網路；沒有時最多等 STARTUP_BUDGET_MS（Quasar 等 boot 完成才掛載 App）
  await startLocale(i18n);
});
