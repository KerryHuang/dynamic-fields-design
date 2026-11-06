import { describe, expect, it } from 'vitest';
import enUS from './en-US.json';
import th from './th.json';
import vi from './vi.json';
import zhTW from './zh-TW.json';

const keys = (o: object, prefix = ''): string[] =>
  Object.entries(o).flatMap(([k, v]) => (typeof v === 'object' && v !== null ? keys(v, `${prefix}${k}.`) : [`${prefix}${k}`]));

describe('document flow i18n', () => {
  it('has the same docflow keys, non-empty, in all four locales', () => {
    const base = keys(zhTW.docflow).sort();
    expect(base.length).toBeGreaterThan(50);
    for (const messages of [enUS, th, vi]) expect(keys(messages.docflow).sort()).toEqual(base);
    for (const messages of [zhTW, enUS, th, vi]) {
      expect(messages.menu.conversions).toBeTruthy();
      expect(keys(messages.docflow).every((k) => k.split('.').reduce<unknown>((o, p) => (o as Record<string, unknown>)[p], messages.docflow))).toBe(true);
    }
  });
});
