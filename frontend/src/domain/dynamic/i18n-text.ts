import { LOCALES } from 'src/i18n/locale';
import { defaultLocaleOrNull, requiredTextLocales } from 'src/i18n/locale-settings';
import type { I18nText, Locale } from './types';

/** 與後端同一條退回鏈：要求的語系 → 系統預設語系 → 繁中 → 第一個有值的（照固定支援語系順序，同後端）；空字串視為沒有。 */
export function resolveI18n(text: I18nText | undefined, locale: string): string {
  const pick = (l: string | null) => (l ? text?.[l as Locale] : undefined);
  return pick(locale) || pick(defaultLocaleOrNull()) || pick('zh-TW') || LOCALES.map((l) => pick(l.code)).find((v) => !!v) || '';
}

/** Trims every locale and drops empty ones; no value left in any locale means no text at all (undefined). */
export function compactI18n(text: I18nText | undefined): I18nText | undefined {
  const out: I18nText = {};
  for (const [l, v] of Object.entries(text ?? {})) if (v?.trim()) out[l as Locale] = v.trim();
  return Object.keys(out).length ? out : undefined;
}

/** 去空白、忽略空值後兩段文字相同。 */
export function sameI18n(a: I18nText | undefined, b: I18nText | undefined): boolean {
  const x = compactI18n(a) ?? {};
  const y = compactI18n(b) ?? {};
  const ks = new Set([...Object.keys(x), ...Object.keys(y)]);
  return [...ks].every((k) => x[k as Locale] === y[k as Locale]);
}

/**
 * 還缺的必填語系。`original`（編輯既有資料時傳載入的值）有值且目前文字沒改，回 []：
 * 必填語系增加後，沒動到的舊資料不先擋（後端也只在新增或改了文字時檢查）。
 */
export function missingRequired(text: I18nText | undefined, original?: I18nText): Locale[] {
  if (original && sameI18n(text, original)) return [];
  return requiredTextLocales().filter((l) => !text?.[l]?.trim());
}
