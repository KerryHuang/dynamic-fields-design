import { defineBoot } from '#q-app';
import { createI18n } from 'vue-i18n';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';
import { applyLocale, readLocale } from 'src/i18n/locale';

export const i18n = createI18n({
  legacy: false,
  locale: 'zh-TW',
  fallbackLocale: 'zh-TW',
  messages: { 'zh-TW': zhTW, 'en-US': enUS, th, vi },
});

export default defineBoot(({ app }) => {
  app.use(i18n);
  // 沿用這個瀏覽器上次的選擇；沒有就是繁中。啟動時不重寫 localStorage
  applyLocale(readLocale(), i18n.global.locale, false);
});
