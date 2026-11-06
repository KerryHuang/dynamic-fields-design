import { flushPromises } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { DashboardList } from 'src/domain/dynamic/dashboard-types';
import { detail, mountPage, q, type World } from './dashboards/dashboard-test-kit';
import HomePage from './HomePage.vue';

const LIST: DashboardList = {
  assigned: [{ id: 1, name: '業務儀表板', version: 1, adjusted: false, hasUpdate: false }, { id: 2, name: '品質儀表板', version: 1, adjusted: false, hasUpdate: false }],
  mine: [],
  sharedReports: [],
};
const world = (over: Partial<World> = {}): World => ({
  list: LIST,
  details: { 1: detail(1, { name: '業務儀表板' }), 2: detail(2, { name: '品質儀表板' }) },
  ...over,
});
const mountHome = (w: World = world(), userId = 'u') => mountPage(w, { page: HomePage, route: '/', userId });

afterEach(() => { localStorage.clear(); document.body.innerHTML = ''; });

describe('HomePage with dashboards', () => {
  it('shows the first dashboard and keeps welcome, apps and entry points', async () => {
    await mountHome();
    expect(q('home-dashboard')).not.toBeNull();
    expect(q('dashboard-name')!.textContent).toContain('業務儀表板');
    expect(q('home-welcome')).not.toBeNull();
    expect(q('home-reports')).not.toBeNull();
  });

  it('opens the dashboard the user looked at last', async () => {
    localStorage.setItem('dashboard.last.u', '2');
    await mountHome();
    expect(q('dashboard-name')!.textContent).toContain('品質儀表板');
  });

  it('falls back to the first when the last one is gone', async () => {
    localStorage.setItem('dashboard.last.u', '42');
    await mountHome();
    expect(q('dashboard-name')!.textContent).toContain('業務儀表板');
  });

  it('is read-only on the home page: no edit button', async () => {
    await mountHome();
    expect(q('dashboard-edit-toggle')).toBeNull();
    expect(q('home-dashboard-open')).not.toBeNull();
  });

  it('switching dashboards remembers the choice', async () => {
    const { w } = await mountHome();
    (w.findComponent({ name: 'HomePage' }).vm as unknown as { pickDash: (id: number) => void }).pickDash(2);
    await flushPromises();
    expect(localStorage.getItem('dashboard.last.u')).toBe('2');
    expect(q('dashboard-name')!.textContent).toContain('品質儀表板');
  });

  it('is the normal home when there are no dashboards', async () => {
    await mountHome(world({ list: { assigned: [], mine: [], sharedReports: [] } }));
    expect(q('home-dashboard')).toBeNull();
    expect(q('home-welcome')).not.toBeNull();
    expect(q('home-reports')).not.toBeNull();
  });

  it('is the normal home when the dashboard list fails to load', async () => {
    await mountHome(world({ list: { status: 500 } }));
    expect(q('home-dashboard')).toBeNull();
    expect(q('home-welcome')).not.toBeNull();
  });
});
