import { describe, expect, it } from 'vitest';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';

type Tree = { [k: string]: string | Tree };
const leaves = (t: Tree, prefix = ''): string[] =>
  Object.entries(t).flatMap(([k, v]) => (typeof v === 'string' ? [`${prefix}${k}`] : leaves(v, `${prefix}${k}.`)));
const dyn = (l: unknown, group: string) => (l as { dynamic: Record<string, Tree> }).dynamic[group] ?? {};

describe('list UI strings exist in all four locales', () => {
  for (const group of ['filter', 'sort', 'columns', 'view', 'export', 'import']) {
    it(`dynamic.${group}`, () => {
      const expected = leaves(dyn(zhTW, group)).sort();
      expect(expected.length).toBeGreaterThan(0);
      for (const loc of [enUS, th, vi]) expect(leaves(dyn(loc, group)).sort()).toEqual(expected);
    });
  }
});

describe('designer strings exist in all four locales', () => {
  for (const group of ['permissions', 'template'] as const) {
    it(`designer.${group}`, () => {
      const d = (l: unknown) => (l as { designer: Record<string, Tree | undefined> }).designer[group] ?? {};
      const expected = leaves(d(zhTW)).sort();
      expect(expected.length).toBeGreaterThan(0);
      for (const loc of [enUS, th, vi]) expect(leaves(d(loc)).sort()).toEqual(expected);
    });
  }
});
