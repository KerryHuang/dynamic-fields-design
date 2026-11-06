import { describe, expect, it } from 'vitest';
import { groupMenu, systemLinks } from './menu-nav';

const item = (key: string, section?: string) => ({ key, name: { 'zh-TW': key }, section });

describe('groupMenu', () => {
  it('puts unsectioned items first (title undefined), then sections by name, keeping /menu order inside a group', () => {
    const g = groupMenu([item('b', 'Z'), item('a1', 'A'), item('x'), item('a2', 'A'), item('y', '')]);
    expect(g.map((x) => x.title)).toEqual([undefined, 'A', 'Z']);
    expect(g[0].items.map((i) => i.key)).toEqual(['x', 'y']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1', 'a2']);
  });
  it('is empty for no items', () => expect(groupMenu([])).toEqual([]));
});

describe('systemLinks', () => {
  const me = (isSystemAdmin: boolean, read: boolean) =>
    ({ isSystemAdmin, designer: { read, add: false, edit: false, delete: false } }) as never;
  it('shows nothing without permission or before me loads', () => {
    expect(systemLinks(me(false, false))).toEqual([]);
    expect(systemLinks(undefined)).toEqual([]);
  });
  it('system admins get users, groups, dashboards, appearance; designer readers get designer and demo data', () => {
    expect(systemLinks(me(true, false)).map((l) => l.to)).toEqual(['/system/users', '/system/groups', '/system/dashboards', '/system/appearance']);
    expect(systemLinks(me(false, true)).map((l) => l.to)).toEqual(['/system/dynamic-modules', '/system/demo-data']);
    expect(systemLinks(me(true, true))).toHaveLength(6);
  });
});
