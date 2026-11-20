import { defineBoot } from '#q-app';
import { createI18n } from 'vue-i18n';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';
import { FALLBACK_LOCALE, applyLocale, readLocale } from 'src/i18n/locale';

export const i18n = createI18n({
  legacy: false,
  locale: FALLBACK_LOCALE,
  fallbackLocale: FALLBACK_LOCALE,
  messages: { 'zh-TW': zhTW, 'en-US': enUS, th, vi },
});

export default defineBoot(({ app }) => {
  app.use(i18n);
  // 沿用這個瀏覽器上次的選擇；沒記過先用翻譯包退回，第一次導覽取得預設語系政策後再套用（auth-guard）。啟動時不重寫 localStorage
  applyLocale(readLocale() ?? FALLBACK_LOCALE, i18n.global.locale, false);
});
