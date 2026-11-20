import { computed, shallowRef } from 'vue';
import type { LocaleCode } from './locale';

// 語系清單與語系政策的唯一入口（語系包設計 §4.4）。語系資訊（自稱、Quasar 語言包、上層語系）登入前來自 GET auth/options
// （只有啟用語系），登入後來自全系統生效值（全部已安裝語系）。語系政策（locale.default、locale.enabled、locale.requiredText）的
// 預設與啟用語系登入前由 auth/options 取得、登入後由全系統生效值更新；必填語系只在登入後載入。沒設定就呼叫會丟錯。

export interface LocaleInfo {
  code: LocaleCode;
  nativeName: string;
  quasarLang: string | null;
  fallbackCode: string | null;
  /** 這個語系在目前預設語系下補齊後的介面文字雜湊（GET i18n/ui/{code}?v=…）；內建清單（伺服器連不上時）是空字串＝不知道 */
  uiHash: string;
}

// shallowRef：60 秒重取更新政策後，已掛載的元件（語言選單、多語輸入框）的 computed 會跟著重算
const infos = shallowRef<LocaleInfo[]>([]);
const def = shallowRef<LocaleCode | null>(null);
const enabled = shallowRef<LocaleCode[] | null>(null);
const required = shallowRef<LocaleCode[] | null>(null);

const known = (v: unknown): v is LocaleCode => typeof v === 'string' && infos.value.some((l) => l.code === v);
/** 只留語系資訊裡有的代碼，保持順序、去重。 */
export const toLocaleCodes = (v: unknown): LocaleCode[] => (Array.isArray(v) ? [...new Set(v.filter(known))] : []);

/** 整份取代語系資訊（順序＝已安裝語系的順序）。 */
export function setLocaleInfos(list: LocaleInfo[]): void {
  infos.value = list.map((l) => ({ ...l }));
}

export const installedLocales = (): LocaleCode[] => infos.value.map((l) => l.code);
export const localeInfo = (code: string): LocaleInfo | undefined => infos.value.find((l) => l.code === code);
/** 語系的自稱；不認得回代碼本身。 */
export const nativeName = (code: string): string => localeInfo(code)?.nativeName ?? code;

export function setLocaleSettings(s: { defaultLocale?: LocaleCode; enabled?: LocaleCode[]; requiredText?: LocaleCode[] }): void {
  if (s.defaultLocale) def.value = s.defaultLocale;
  if (s.enabled) enabled.value = [...s.enabled];
  if (s.requiredText) required.value = [...s.requiredText];
}

function need<T>(v: T | null, name: string): T {
  if (v === null) throw new Error(`locale setting not loaded: ${name}`);
  return v;
}

export const defaultLocale = (): LocaleCode => need(def.value, 'default');
export const enabledLocales = (): LocaleCode[] => need(enabled.value, 'enabled');
export const requiredTextLocales = (): LocaleCode[] => need(required.value, 'requiredText');
/** 沒載入時回 null：給「資料顯示」的退回鏈用，不讓公開頁面因此丟錯。 */
export const defaultLocaleOrNull = (): LocaleCode | null => def.value;

// 與後端 LocaleChain 相同的技術限制與平台基準語系
const MAX_PARENT_DEPTH = 3;
const BASE_LOCALE: LocaleCode = 'zh-TW';

/**
 * 要求的語系之後的退回鏈，與後端 LocaleChain.Fallbacks 同一條（共用案例 shared/locale-chain-cases.json）：上層語系
 * （最多 3 層，遇到環或沒安裝就停）→ 預設語系 → 繁中 → 全部已安裝語系，去重。語系資訊還沒載入時是「預設 → 繁中」。
 */
export function localeChain(locale: string): LocaleCode[] {
  const list = infos.value;
  const parents = new Map(list.map((l) => [l.code, l.fallbackCode]));
  const chain: LocaleCode[] = [];
  const seen = new Set<string>([locale]);
  let cur = locale;
  for (let depth = 0; depth < MAX_PARENT_DEPTH; depth++) {
    const parent = parents.get(cur);
    if (!parent || !parents.has(parent) || seen.has(parent)) break;
    seen.add(parent);
    chain.push(parent);
    cur = parent;
  }
  if (def.value) chain.push(def.value);
  chain.push(BASE_LOCALE, ...list.map((l) => l.code));
  return [...new Set(chain)];
}

/** 元件用：已安裝、啟用、必填語系與預設語系，政策或語系資訊更新後跟著重算。 */
export function useLocales() {
  return {
    installed: computed(() => installedLocales()),
    enabled: computed(() => enabledLocales()),
    required: computed(() => requiredTextLocales()),
    defaultLocale: computed(() => defaultLocale()),
  };
}

/** 從全系統生效值（GET policies/effective）更新語系資訊（有給 locales 時）與三個語系設定；缺的項目不動。 */
export function applyLocalePolicies(values: Record<string, unknown>, locales?: LocaleInfo[]): void {
  if (locales?.length) setLocaleInfos(locales);
  const d = values['locale.default'];
  const e = toLocaleCodes(values['locale.enabled']);
  const r = toLocaleCodes(values['locale.requiredText']);
  setLocaleSettings({ defaultLocale: known(d) ? d : undefined, enabled: e.length ? e : undefined, requiredText: r.length ? r : undefined });
}
