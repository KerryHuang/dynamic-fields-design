import { defineBoot } from '@quasar/app-vite/wrappers';
import { createI18n } from 'vue-i18n';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';

export const i18n = createI18n({
  legacy: false,
  locale: 'zh-TW',
  fallbackLocale: 'zh-TW',
  messages: { 'zh-TW': zhTW, 'en-US': enUS, th, vi },
});

export default defineBoot(({ app }) => {
  app.use(i18n);
});
