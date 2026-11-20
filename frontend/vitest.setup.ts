import { enableAutoUnmount } from '@vue/test-utils';
import { afterAll, afterEach } from 'vitest';
import enUS from 'shared/i18n/en-US/ui.json';
import th from 'shared/i18n/th/ui.json';
import vi from 'shared/i18n/vi/ui.json';
import { i18n } from 'src/i18n/instance';
import { setBusinessOffset } from 'src/domain/dynamic/business-time';
import { BUILTIN_LOCALES } from 'src/i18n/locale';
import { setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';

// 每個測試結束就卸載掛載的元件，不讓上一個測試的元件留到下一個。
enableAutoUnmount(afterEach);

// Quasar 的 Screen 插件在 window 上掛 resize 監聽（卸載元件也不會移除），收到 resize 後延遲 16ms 才更新尺寸。
// 測試檔若在這 16ms 內結束，計時器會在 jsdom 拆除後觸發，丟 "window is not defined" 讓整批算失敗（CI 偶發）。
// 每個檔案結束前多等一下，讓已排的更新在 window 還在時跑完。
afterAll(() => new Promise((resolve) => setTimeout(resolve, 50)));

// 營運時區：等於後端出廠值，只給測試用（正式環境由登入後的第一次導覽從 API 載入）。
setBusinessOffset('+08:00');

// 語系政策出廠值（預設繁中、四語啟用、必填繁中），只給測試用；正式環境由 auth/options 與全系統生效值載入。
setLocaleInfos(BUILTIN_LOCALES);
setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] });

// 介面文字：正式環境只打包繁中、其他語系執行期載入；只給測試用，單例預載其他三語，讓元件測試照常切語言斷言文案
// （載入行為由 src/i18n/ui-messages、startup 的 spec 用乾淨的 i18n 實例測）。
i18n.global.setLocaleMessage('en-US', enUS);
i18n.global.setLocaleMessage('th', th);
i18n.global.setLocaleMessage('vi', vi);
