import { describe, expect, it } from 'vitest';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';

function leaves(o: unknown, path = ''): Record<string, string> {
  if (typeof o === 'string') return { [path]: o };
  return Object.entries(o as object).reduce((acc, [k, v]) => ({ ...acc, ...leaves(v, path ? `${path}.${k}` : k) }), {});
}
const pick = (m: typeof zhTW): Record<string, string> => ({
  ...leaves(m.dashboards, 'dashboards'),
  'menu.dashboards': m.menu.dashboards, 'menu.dashboardsAdmin': m.menu.dashboardsAdmin,
  'groups.impactDashboards': m.groups.impactDashboards,
  'reports.state.unshared': m.reports.state.unshared, 'reports.state.reportGone': m.reports.state.reportGone,
});
const zh = pick(zhTW);
const en = pick(enUS);
const placeholders = (s: string) => (s.match(/\{\w+\}/g) ?? []).sort();

describe('儀表板文案', () => {
  it.each([['en-US', enUS], ['th', th], ['vi', vi]])('%s 的鍵與 zh-TW 完全相同', (_n, msgs) => {
    expect(Object.keys(pick(msgs as typeof zhTW)).sort()).toEqual(Object.keys(zh).sort());
  });

  it.each([['en-US', enUS], ['th', th], ['vi', vi]])('%s 的佔位符與 zh-TW 一致、文案不空', (_n, msgs) => {
    for (const [k, v] of Object.entries(pick(msgs as typeof zhTW))) {
      expect(v.trim(), k).not.toBe('');
      expect(placeholders(v), k).toEqual(placeholders(zh[k]));
    }
  });

  it.each([['th', th], ['vi', vi]])('%s 的文案不是直接抄 zh-TW 或 en-US', (_n, msgs) => {
    for (const [k, v] of Object.entries(pick(msgs as typeof zhTW))) {
      expect(v, k).not.toBe(zh[k]);
      expect(v, k).not.toBe(en[k]);
    }
  });
});
