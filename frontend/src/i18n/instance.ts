import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { FALLBACK_LOCALE } from './locale';

// 全站的 vue-i18n 單例。只打包繁中（介面文字的基準與最後退回）；其他語系、繁中的覆寫都在執行期從伺服器載入
// （語系包設計 §8.2，src/i18n/startup.ts）。獨立成檔不依賴 API 模組，boot 與 vitest.setup 共用同一個實例。
export const i18n = createI18n({
  legacy: false,
  locale: FALLBACK_LOCALE,
  fallbackLocale: FALLBACK_LOCALE,
  // 鍵型別放寬成字串：語系代碼是資料（已安裝語系），不是打包的翻譯包
  messages: { 'zh-TW': zhTW } as Record<string, typeof zhTW>,
});
