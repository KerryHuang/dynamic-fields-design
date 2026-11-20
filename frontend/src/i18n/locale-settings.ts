import { LOCALES, type LocaleCode } from './locale';

// 語系政策（locale.default、locale.enabled、locale.requiredText）。前端取「啟用語系」只有這一處（i4a 換成 useLocales()）。
// 預設與啟用語系登入前由 GET auth/options 取得、登入後由全系統生效值更新；必填語系只在登入後載入。沒設定就呼叫會丟錯。

const known = (v: unknown): v is LocaleCode => LOCALES.some((l) => l.code === v);
/** 只留認得的語系代碼，保持順序、去重。 */
export const toLocaleCodes = (v: unknown): LocaleCode[] => (Array.isArray(v) ? [...new Set(v.filter(known))] : []);

let def: LocaleCode | null = null;
let enabled: LocaleCode[] | null = null;
let required: LocaleCode[] | null = null;

export function setLocaleSettings(s: { defaultLocale?: LocaleCode; enabled?: LocaleCode[]; requiredText?: LocaleCode[] }): void {
  if (s.defaultLocale) def = s.defaultLocale;
  if (s.enabled) enabled = [...s.enabled];
  if (s.requiredText) required = [...s.requiredText];
}

function need<T>(v: T | null, name: string): T {
  if (v === null) throw new Error(`locale setting not loaded: ${name}`);
  return v;
}

export const defaultLocale = (): LocaleCode => need(def, 'default');
export const enabledLocales = (): LocaleCode[] => need(enabled, 'enabled');
export const requiredTextLocales = (): LocaleCode[] => need(required, 'requiredText');
/** 沒載入時回 null：給「資料顯示」的退回鏈用，不讓公開頁面因此丟錯。 */
export const defaultLocaleOrNull = (): LocaleCode | null => def;

/** 從全系統生效值（GET policies/effective）更新三個語系設定；缺的項目不動。 */
export function applyLocalePolicies(values: Record<string, unknown>): void {
  const d = values['locale.default'];
  const e = toLocaleCodes(values['locale.enabled']);
  const r = toLocaleCodes(values['locale.requiredText']);
  setLocaleSettings({ defaultLocale: known(d) ? d : undefined, enabled: e.length ? e : undefined, requiredText: r.length ? r : undefined });
}
