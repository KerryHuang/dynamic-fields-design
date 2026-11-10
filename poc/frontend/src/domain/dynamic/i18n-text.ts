import type { I18nText, Locale } from './types';

/** Same rule as the backend: missing or empty = absent, fall back to zh-TW. */
export function resolveI18n(text: I18nText | undefined, locale: string): string {
  const pick = (l: string) => text?.[l as Locale];
  return pick(locale) || pick('zh-TW') || '';
}

/** Trims every locale and drops empty ones; no zh-TW left means no text at all (undefined). */
export function compactI18n(text: I18nText | undefined): I18nText | undefined {
  const out: I18nText = {};
  for (const [l, v] of Object.entries(text ?? {})) if (v?.trim()) out[l as Locale] = v.trim();
  return out['zh-TW'] ? out : undefined;
}
