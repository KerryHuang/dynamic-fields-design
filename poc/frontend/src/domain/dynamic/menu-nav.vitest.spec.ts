import { describe, expect, it } from 'vitest';
import { groupMenu, systemLinks } from './menu-nav';

const item = (key: string, section?: string, closed?: boolean) =>
  ({ key, name: { 'zh-TW': key }, section: section === undefined ? undefined : { 'zh-TW': section }, closed });
const sec = (key: string, section: Record<string, string>) => ({ key, name: { 'zh-TW': key }, section });

describe('groupMenu', () => {
  it('puts unsectioned items first (title undefined), then sections by name, keeping /menu order inside a group', () => {
    const g = groupMenu([item('b', 'Z'), item('a1', 'A'), item('x'), item('a2', 'A'), item('y', '')], 'zh-TW');
    expect(g.map((x) => x.title)).toEqual([undefined, 'A', 'Z']);
    expect(g[0].items.map((i) => i.key)).toEqual(['x', 'y']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1', 'a2']);
  });
  it('pulls closed-app items into one trailing group, whatever their section', () => {
    const g = groupMenu([item('c1', 'A', true), item('x'), item('a1', 'A'), item('c2', undefined, true)], 'zh-TW');
    expect(g.map((x) => x.closed ?? false)).toEqual([false, false, true]);
    expect(g[2].items.map((i) => i.key)).toEqual(['c1', 'c2']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1']);
  });
  it('has no closed group when every app is open', () => expect(groupMenu([item('a')], 'zh-TW').some((x) => x.closed)).toBe(false));
  it('is empty for no items', () => expect(groupMenu([], 'zh-TW')).toEqual([]));
  it('shows the section in the current locale, falling back to zh-TW when it has no translation', () => {
    const g = groupMenu([sec('p', { 'zh-TW': '主檔', 'en-US': 'Master data' }), item('legacy', '庫存')], 'en-US');
    expect(g.map((x) => x.title)).toEqual(['Master data', '庫存']);
  });
  it('groups by the zh-TW section across apps, titled by the first module, and keeps zh-TW order in every locale', () => {
    const g = groupMenu([
      sec('s1', { 'zh-TW': '銷售', 'en-US': 'Sales' }),
      sec('m1', { 'zh-TW': '主檔', 'en-US': 'Master data' }),
      sec('m2', { 'zh-TW': '主檔', 'en-US': 'Masters' }),
      item('m3', '主檔'),
    ], 'en-US');
    expect(g.map((x) => x.key)).toEqual(['主檔', '銷售']);
    expect(g.map((x) => x.title)).toEqual(['Master data', 'Sales']);
    expect(g[0].items.map((i) => i.key)).toEqual(['m1', 'm2', 'm3']);
  });
});

describe('systemLinks', () => {
  const me = (isSystemAdmin: boolean, read: boolean) =>
    ({ isSystemAdmin, designer: { read, add: false, edit: false, delete: false } }) as never;
  it('shows nothing without permission or before me loads', () => {
    expect(systemLinks(me(false, false))).toEqual([]);
    expect(systemLinks(undefined)).toEqual([]);
  });
  it('system admins get users, groups, organization, dashboards, appearance; designer readers get designer, conversions and demo data', () => {
    expect(systemLinks(me(true, false)).map((l) => l.to))
      .toEqual(['/system/users', '/system/groups', '/system/organization', '/system/dashboards', '/system/appearance']);
    expect(systemLinks(me(true, false)).find((l) => l.to === '/system/organization'))
      .toEqual({ to: '/system/organization', label: 'menu.organization', icon: 'account_tree', testid: 'organization-menu' });
    expect(systemLinks(me(false, true)).map((l) => l.testid)).not.toContain('organization-menu');
    expect(systemLinks(me(true, true)).map((l) => l.to)).toContain('/system/apps');
    expect(systemLinks(me(false, true)).map((l) => l.to)).not.toContain('/system/apps');
    expect(systemLinks(me(false, true)).map((l) => l.to)).toEqual(['/system/dynamic-modules', '/system/conversions', '/system/demo-data']);
    expect(systemLinks(me(true, true))).toHaveLength(9);
  });
});
