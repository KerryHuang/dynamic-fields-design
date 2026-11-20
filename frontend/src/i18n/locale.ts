import { Quasar } from 'quasar';
import builtinLocales from '../../../shared/builtin-locales.json';
import { localeInfo, type LocaleInfo } from './locale-settings';

/** 語系代碼（字串；合不合法由「在不在 API 給的清單」判斷，語系包 i4a 計畫判斷 14）。 */
export type LocaleCode = string;

/**
 * 內建四語（隨程式出貨、一定已安裝），只給伺服器連不上時的退路與測試用；平時的語系清單一律從 API 取（locale-settings）。
 * 單一來源是 shared/builtin-locales.json，後端 BuiltinLocales 以 fixture 測試對齊。
 */
export const BUILTIN_LOCALES: LocaleInfo[] = builtinLocales.map((l) => ({ ...l, fallbackCode: null }));

/** 選單用各語言的自稱（暫留給還沒改讀 useLocales() 的使用點，i4a Task 8 刪除）。 */
export const LOCALES: { code: LocaleCode; label: string }[] = BUILTIN_LOCALES.map((l) => ({ code: l.code, label: l.nativeName }));
/** 伺服器連不上、還沒取得預設語系時的翻譯包退回（不是語系政策的預設）。 */
export const FALLBACK_LOCALE: LocaleCode = 'zh-TW';
export const LOCALE_KEY = 'formwright.locale';

const MAX_CODE_LENGTH = 35;

/** 這個瀏覽器記過的語系；讀不到、只有空白、過長、localStorage 不可用（無痕、被封鎖）回 null。是否啟用由 auth guard 判斷。 */
export function readLocale(): LocaleCode | null {
  try {
    const v = localStorage.getItem(LOCALE_KEY)?.trim();
    return v && v.length <= MAX_CODE_LENGTH ? v : null;
  } catch {
    return null;
  }
}

type QuasarPack = { default: Parameters<typeof Quasar.lang.set>[0] };
/** Quasar 全部語言包，各自 lazy chunk（語系包設計 §8.5）。匯出只為了測試能替換載入函式。 */
export const quasarPacks = import.meta.glob('../../node_modules/quasar/lang/*.js') as Record<string, () => Promise<QuasarPack>>;
const packPath = (lang: string) => `../../node_modules/quasar/lang/${lang}.js`;
/** 語系沒有 Quasar 語言包、或載入失敗時改用的包（Quasar 的語言包代碼，不是平台語系清單）。 */
const QUASAR_FALLBACK = 'en-US';
let loadSeq = 0;

/**
 * 設定 <html lang> 與 Quasar 內建文字（日期選擇器、表格分頁等）的語言包：取語系資訊的 quasarLang，沒有資訊時用代碼本身；
 * 找不到檔或載入失敗改載 en-US。快速連切時只有最後一次呼叫的結果會套用。
 */
export function applyDocumentLocale(code: LocaleCode) {
  document.documentElement.lang = code;
  const seq = ++loadSeq;
  const info = localeInfo(code);
  const lang = info ? info.quasarLang : code;
  const fallback = quasarPacks[packPath(QUASAR_FALLBACK)];
  const load = (lang && quasarPacks[packPath(lang)]) || fallback;
  const apply = (m: QuasarPack) => { if (seq === loadSeq) Quasar.lang.set(m.default); };
  void load?.().then(apply)
    .catch(() => fallback?.().then(apply))
    .catch(() => { /* 退路也載不到：維持原本的內建文字 */ });
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
