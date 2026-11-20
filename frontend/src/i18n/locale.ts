import { Quasar } from 'quasar';

export type LocaleCode = 'zh-TW' | 'en-US' | 'th' | 'vi';

/** 選單用各語言的自稱，不隨目前語系翻譯。 */
export const LOCALES: { code: LocaleCode; label: string }[] = [
  { code: 'zh-TW', label: '繁體中文' },
  { code: 'en-US', label: 'English' },
  { code: 'th', label: 'ไทย' },
  { code: 'vi', label: 'Tiếng Việt' },
];
/** 伺服器連不上、還沒取得預設語系時的翻譯包退回（不是語系政策的預設）。 */
export const FALLBACK_LOCALE: LocaleCode = 'zh-TW';
export const LOCALE_KEY = 'formwright.locale';

const isLocale = (v: unknown): v is LocaleCode => LOCALES.some((l) => l.code === v);

/** 這個瀏覽器記過的語系；讀不到、值不認得、localStorage 不可用（無痕、被封鎖）回 null（由呼叫端套用預設語系）。 */
export function readLocale(): LocaleCode | null {
  try {
    const v = localStorage.getItem(LOCALE_KEY);
    return isLocale(v) ? v : null;
  } catch {
    return null;
  }
}

const packs = import.meta.glob('../../node_modules/quasar/lang/{zh-TW,en-US,th,vi}.js') as Record<string, () => Promise<{ default: Parameters<typeof Quasar.lang.set>[0] }>>;

/** 設定 <html lang> 與 Quasar 內建文字（日期選擇器、表格分頁等）的語言包。 */
export function applyDocumentLocale(code: LocaleCode) {
  document.documentElement.lang = code;
  const load = packs[`../../node_modules/quasar/lang/${code}.js`];
  void load?.().then((m) => Quasar.lang.set(m.default)).catch(() => { /* 語言包載入失敗：維持原本的內建文字 */ });
}

/** 切換語系：更新 vue-i18n 的 locale ref、文件語言、Quasar 語言包，並記在這個瀏覽器。 */
export function applyLocale(code: LocaleCode, locale: { value: string }, persist = true) {
  locale.value = code;
  applyDocumentLocale(code);
  if (!persist) return;
  try {
    localStorage.setItem(LOCALE_KEY, code);
  } catch {
    /* 不可用時照常切換，只是不記住 */
  }
}
