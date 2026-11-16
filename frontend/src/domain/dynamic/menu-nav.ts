import { resolveI18n } from './i18n-text';
import type { MenuItem, Me } from './types';

export interface MenuGroup {
  /** The zh-TW section text: the same section from different apps forms one group. undefined = unsectioned. */
  key: string | undefined;
  /** Display title in the current locale, taken from the group's first module. */
  title: string | undefined;
  items: MenuItem[];
  /** The trailing group of modules whose app is closed (read-only). */
  closed?: boolean;
}

/**
 * Unsectioned items form the leading group (title undefined); sections follow by their zh-TW text in every locale,
 * so the order does not jump when switching language; /menu order holds inside a group.
 * Items of closed apps are pulled out into one trailing group so they never mix with writable modules.
 */
export function groupMenu(items: MenuItem[], locale: string): MenuGroup[] {
  const groups = new Map<string | undefined, MenuItem[]>();
  const closed: MenuItem[] = [];
  for (const m of items) {
    if (m.closed) { closed.push(m); continue; }
    const key = m.section?.['zh-TW'] || undefined;
    const g = groups.get(key);
    if (g) g.push(m); else groups.set(key, [m]);
  }
  const open: MenuGroup[] = [...groups.entries()]
    .map(([key, list]) => ({ key, title: key === undefined ? undefined : resolveI18n(list[0]!.section, locale), items: list }))
    .sort((a, b) => (a.key === undefined ? -1 : b.key === undefined ? 1 : a.key.localeCompare(b.key)));
  return closed.length ? [...open, { key: undefined, title: undefined, items: closed, closed: true }] : open;
}

export interface SystemLink {
  to: string;
  /** i18n key */
  label: string;
  icon: string;
  testid: string;
}

/** 系統管理 entries the signed-in user may see; shared by the side menu and the home page so permissions cannot drift. */
export function systemLinks(me: Pick<Me, 'isSystemAdmin' | 'designer'> | undefined): SystemLink[] {
  if (!me) return [];
  const links: SystemLink[] = [];
  if (me.isSystemAdmin) {
    links.push(
      { to: '/system/users', label: 'menu.users', icon: 'person', testid: 'users-menu' },
      { to: '/system/groups', label: 'menu.groups', icon: 'groups', testid: 'groups-menu' },
      { to: '/system/organization', label: 'menu.organization', icon: 'account_tree', testid: 'organization-menu' },
      { to: '/system/dashboards', label: 'menu.dashboardsAdmin', icon: 'space_dashboard', testid: 'dashboards-admin-menu' },
      { to: '/system/appearance', label: 'theme.adminMenu', icon: 'palette', testid: 'appearance-menu' },
      { to: '/system/policies', label: 'policies.menu', icon: 'tune', testid: 'policies-menu' },
    );
  }
  if (me.isSystemAdmin && me.designer.read)
    links.push({ to: '/system/apps', label: 'menu.apps', icon: 'inventory_2', testid: 'apps-menu' });
  if (me.designer.read) {
    links.push(
      { to: '/system/dynamic-modules', label: 'menu.dynamicModules', icon: 'design_services', testid: 'designer-menu' },
      { to: '/system/conversions', label: 'menu.conversions', icon: 'swap_horiz', testid: 'conversions-menu' },
      { to: '/system/demo-data', label: 'menu.demoData', icon: 'dataset', testid: 'demo-data-menu' },
    );
  }
  return links;
}
