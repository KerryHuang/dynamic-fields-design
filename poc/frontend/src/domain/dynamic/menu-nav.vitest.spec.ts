import { describe, expect, it } from 'vitest';
import { groupMenu, systemLinks } from './menu-nav';

const item = (key: string, section?: string, closed?: boolean) => ({ key, name: { 'zh-TW': key }, section, closed });

describe('groupMenu', () => {
  it('puts unsectioned items first (title undefined), then sections by name, keeping /menu order inside a group', () => {
    const g = groupMenu([item('b', 'Z'), item('a1', 'A'), item('x'), item('a2', 'A'), item('y', '')]);
    expect(g.map((x) => x.title)).toEqual([undefined, 'A', 'Z']);
    expect(g[0].items.map((i) => i.key)).toEqual(['x', 'y']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1', 'a2']);
  });
  it('pulls closed-app items into one trailing group, whatever their section', () => {
    const g = groupMenu([item('c1', 'A', true), item('x'), item('a1', 'A'), item('c2', undefined, true)]);
    expect(g.map((x) => x.closed ?? false)).toEqual([false, false, true]);
    expect(g[2].items.map((i) => i.key)).toEqual(['c1', 'c2']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1']);
  });
  it('has no closed group when every app is open', () => expect(groupMenu([item('a')]).some((x) => x.closed)).toBe(false));
  it('is empty for no items', () => expect(groupMenu([])).toEqual([]));
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
