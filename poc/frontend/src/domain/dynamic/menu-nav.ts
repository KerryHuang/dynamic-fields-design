import type { MenuItem, Me } from './types';

export interface MenuGroup {
  title: string | undefined;
  items: MenuItem[];
  /** The trailing group of modules whose app is closed (read-only). */
  closed?: boolean;
}

/**
 * Unsectioned items form the leading group (title undefined); sections follow by name; /menu order holds inside a group.
 * Items of closed apps are pulled out into one trailing group so they never mix with writable modules.
 */
export function groupMenu(items: MenuItem[]): MenuGroup[] {
  const groups = new Map<string | undefined, MenuItem[]>();
  const closed: MenuItem[] = [];
  for (const m of items) {
    if (m.closed) { closed.push(m); continue; }
    const g = groups.get(m.section || undefined);
    if (g) g.push(m); else groups.set(m.section || undefined, [m]);
  }
  const open: MenuGroup[] = [...groups.entries()]
    .map(([title, list]) => ({ title, items: list }))
    .sort((a, b) => (a.title === undefined ? -1 : b.title === undefined ? 1 : a.title.localeCompare(b.title)));
  return closed.length ? [...open, { title: undefined, items: closed, closed: true }] : open;
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
      { to: '/system/dashboards', label: 'menu.dashboardsAdmin', icon: 'space_dashboard', testid: 'dashboards-admin-menu' },
      { to: '/system/appearance', label: 'theme.adminMenu', icon: 'palette', testid: 'appearance-menu' },
    );
  }
  if (me.isSystemAdmin && me.designer.read)
    links.push({ to: '/system/apps', label: 'menu.apps', icon: 'inventory_2', testid: 'apps-menu' });
  if (me.designer.read) {
    links.push(
      { to: '/system/dynamic-modules', label: 'menu.dynamicModules', icon: 'design_services', testid: 'designer-menu' },
      { to: '/system/demo-data', label: 'menu.demoData', icon: 'dataset', testid: 'demo-data-menu' },
    );
  }
  return links;
}
