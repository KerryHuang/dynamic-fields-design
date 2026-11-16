import { i18n } from 'src/boot/i18n';
import type { LocaleCode } from 'src/i18n/locale';

export type Translate = (key: string, params?: Record<string, unknown>) => string;

/** 指定語系的翻譯函式；給沒有元件實例可用 useI18n 的純函式（欄位顯示、驗證訊息）。 */
export const tFor = (locale: string): Translate => (key, params) => i18n.global.t(key, params ?? {}, { locale: locale as LocaleCode });

/** 目前介面語系。 */
export const currentLocale = (): string => i18n.global.locale.value;

/** 清單串接：分隔符取自語系檔 common.listSeparator（繁中「、」，其他「, 」）；不給語系就用目前介面語系。 */
export const joinList = (items: string[], locale: string = currentLocale()): string =>
  items.join(tFor(locale)('common.listSeparator'));
