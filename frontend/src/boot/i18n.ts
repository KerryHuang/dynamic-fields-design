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
  // 鍵型別放寬成字串：語系代碼是資料（已安裝語系），不是這四個翻譯包的聯集
  messages: { 'zh-TW': zhTW, 'en-US': enUS, th, vi } as Record<string, typeof zhTW>,
});

export default defineBoot(({ app }) => {
  app.use(i18n);
  // 沿用這個瀏覽器上次的選擇；沒記過先用翻譯包退回，第一次導覽取得預設語系政策後再套用（auth-guard）。啟動時不重寫 localStorage
  applyLocale(readLocale() ?? FALLBACK_LOCALE, i18n.global.locale, false);
});
