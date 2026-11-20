import { describe, expect, it } from 'vitest';
import enUS from 'shared/i18n/en-US/ui.json';
import th from 'shared/i18n/th/ui.json';
import vi from 'shared/i18n/vi/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';

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

describe('print i18n', () => {
  it('has the same non-empty print keys in all four locales', () => {
    const base = keys(zhTW.print).sort();
    expect(base.length).toBeGreaterThan(20);
    for (const messages of [enUS, th, vi]) expect(keys(messages.print).sort()).toEqual(base);
    for (const messages of [zhTW, enUS, th, vi]) for (const k of base) expect(k.split('.').reduce<unknown>((o, p) => (o as Record<string, unknown>)[p], messages.print)).toBeTruthy();
  });
});
