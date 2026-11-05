import type { I18nText, Locale } from './types';

/** Same rule as the backend: missing or empty = absent, fall back to zh-TW. */
export function resolveI18n(text: I18nText | undefined, locale: string): string {
  const pick = (l: string) => text?.[l as Locale];
  return pick(locale) || pick('zh-TW') || '';
}
