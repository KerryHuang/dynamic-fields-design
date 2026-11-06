import { flushPromises } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DashboardDetail, DashboardList } from 'src/domain/dynamic/dashboard-types';
import DashboardView from 'src/presentation/dashboards/DashboardView.vue';
import DashboardGrid from 'src/presentation/dashboards/DashboardGrid.vue';
import { card, click, detail, mountPage, okResult, q, qa, type World } from './dashboard-test-kit';
import DashboardPage from './DashboardPage.vue';

const LIST: DashboardList = {
  assigned: [{ id: 1, name: '業務儀表板', version: 1, adjusted: false, hasUpdate: false }, { id: 2, name: '品質儀表板', version: 1, adjusted: false, hasUpdate: false }],
  mine: [{ id: 3, name: '我的看板' }],
  sharedReports: [],
};
const world = (over: Partial<World> = {}): World & { details: Record<number, DashboardDetail> } => ({
  list: LIST,
  details: {
    1: detail(1, { name: '業務儀表板', layout: { cards: [card('c1'), card('c2', 2, { x: 6 })] }, reportNames: { 1: '訂單狀態', 2: '客戶金額' } }),
    2: detail(2, { name: '品質儀表板' }),
    3: detail(3, { name: '我的看板', kind: 'mine' as const }),
  },
  ...over,
});
const mountDash = (w = world(), route = '/dashboards', userId = 'u') => mountPage(w, { page: DashboardPage, route, userId });

afterEach(() => { localStorage.clear(); document.body.innerHTML = ''; });

describe('DashboardPage', () => {
  it('opens the first dashboard when there is no last one', async () => {
    const { router } = await mountDash();
    expect(router.currentRoute.value.path).toBe('/dashboards/1');
    expect(q('dashboard-name')!.textContent).toContain('業務儀表板');
  });

  it('opens the last viewed dashboard, keyed per user', async () => {
    localStorage.setItem('dashboard.last.u', '2');
    localStorage.setItem('dashboard.last.other', '3');
    const { router } = await mountDash();
    expect(router.currentRoute.value.path).toBe('/dashboards/2');
  });

  it('falls back to the first when the last one is no longer visible', async () => {
    localStorage.setItem('dashboard.last.u', '99');
    const { router } = await mountDash();
    expect(router.currentRoute.value.path).toBe('/dashboards/1');
  });

  it('remembers the dashboard opened last', async () => {
    await mountDash(world(), '/dashboards/2');
    expect(localStorage.getItem('dashboard.last.u')).toBe('2');
  });

  it('still renders when localStorage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    const { router } = await mountDash();
    expect(router.currentRoute.value.path).toBe('/dashboards/1');
    expect(q('dashboard-name')).not.toBeNull();
    vi.restoreAllMocks();
  });

  it('one failing card does not affect the others', async () => {
    await mountDash(world({ cards: { c1: new Error('boom'), c2: okResult() } }));
    expect(q('card-c1')!.querySelector('[data-testid="card-error"]')).not.toBeNull();
    expect(q('card-c2')!.querySelector('[data-testid="card-error"]')).toBeNull();
    expect(q('card-c2')!.textContent).toContain('進行中');
  });

  it('shows the ignores-filter note when the card has its own range', async () => {
    await mountDash(world({ cards: { c1: okResult({ ignoresDateFilter: true }), c2: okResult() } }));
    expect(q('card-c1')!.querySelector('[data-testid="card-ignores-filter"]')).not.toBeNull();
    expect(q('card-c2')!.querySelector('[data-testid="card-ignores-filter"]')).toBeNull();
  });

  it('shows unshared and reportGone card states', async () => {
    await mountDash(world({ cards: { c1: okResult({ state: 'unshared' as never, groups: null, total: null }), c2: okResult({ state: 'reportGone' as never, groups: null, total: null }) } }));
    expect(q('card-c1')!.textContent).toContain('不再分享給你');
    expect(q('card-c2')!.textContent).toContain('已被刪除');
  });

  it('hides the card link when the target dashboard is not visible', async () => {
    const w = world();
    w.details[1] = detail(1, {
      layout: { cards: [card('c1', 1, { options: { sortDesc: false, cumulative: false, linkDashboardId: 2 } }), card('c2', 2, { options: { sortDesc: false, cumulative: false, linkDashboardId: 99 } })] },
      reportNames: { 1: 'a', 2: 'b' },
    });
    await mountDash(w);
    expect(q('card-c1')!.querySelector('[data-testid="card-link"]')).not.toBeNull();
    expect(q('card-c2')!.querySelector('[data-testid="card-link"]')).toBeNull();
  });

  it('a temporary date filter is sent with each card run and never saved', async () => {
    const { w, calls } = await mountDash();
    const runs = () => calls.filter((c) => c.url.endsWith('/run'));
    expect(runs().every((c) => (c.body as { filterRange: unknown }).filterRange === null)).toBe(true);
    const before = runs().length;
    (w.findComponent(DashboardView).vm as unknown as { filterKey: string | null }).filterKey = 'thisMonth';
    await flushPromises();
    const after = runs().slice(before);
    expect(after).toHaveLength(2);
    expect(after.every((c) => (c.body as { filterRange: { kind: string } }).filterRange.kind === 'thisMonth')).toBe(true);
    expect(calls.some((c) => c.method === 'PUT')).toBe(false);
    expect(q('dashboard-adjusted')).toBeNull();
  });

  it('dragging saves the layout and shows the adjusted mark', async () => {
    const { w, calls } = await mountDash();
    await click('dashboard-edit-toggle');
    w.findComponent(DashboardGrid).vm.$emit('change', [{ i: 'c1', x: 3, y: 0, w: 6, h: 4 }, { i: 'c2', x: 6, y: 0, w: 6, h: 4 }]);
    await flushPromises();
    const put = calls.find((c) => c.method === 'PUT' && c.url === '/dashboards/1/layout');
    expect(put).toBeDefined();
    expect((put!.body as { layout: { cards: { id: string; x: number }[] } }).layout.cards.find((c) => c.id === 'c1')!.x).toBe(3);
    expect(q('dashboard-adjusted')).not.toBeNull();
  });

  it('saving sends the version the screen loaded, so a later admin update is not swallowed', async () => {
    const w0 = world();
    w0.details[1] = detail(1, { name: '業務儀表板', version: 3, layout: { cards: [card('c1'), card('c2', 2, { x: 6 })] }, reportNames: { 1: 'a', 2: 'b' } });
    const { w, calls } = await mountDash(w0);
    await click('dashboard-edit-toggle');
    w.findComponent(DashboardGrid).vm.$emit('change', [{ i: 'c1', x: 3, y: 0, w: 6, h: 4 }, { i: 'c2', x: 6, y: 0, w: 6, h: 4 }]);
    await flushPromises();
    const put = calls.find((c) => c.method === 'PUT' && c.url === '/dashboards/1/layout');
    expect((put!.body as { version: number }).version).toBe(3);
  });

  it('does not save when a drag ends where it started', async () => {
    const { w, calls } = await mountDash();
    await click('dashboard-edit-toggle');
    w.findComponent(DashboardGrid).vm.$emit('change', [{ i: 'c1', x: 0, y: 0, w: 6, h: 4 }, { i: 'c2', x: 6, y: 0, w: 6, h: 4 }]);
    await flushPromises();
    expect(calls.some((c) => c.method === 'PUT')).toBe(false);
  });

  it('hasUpdate shows the restore banner; restore asks to confirm, then reloads', async () => {
    const w = world();
    w.details[1] = detail(1, { adjusted: true, hasUpdate: true });
    const { calls } = await mountDash(w);
    expect(q('dashboard-update-banner')).not.toBeNull();
    await click('dashboard-restore');
    expect(calls.some((c) => c.url === '/dashboards/1/restore')).toBe(false);
    await click('restore-confirm');
    expect(calls.some((c) => c.method === 'POST' && c.url === '/dashboards/1/restore')).toBe(true);
    expect(q('dashboard-update-banner')).toBeNull();
  });

  it('shows no restore banner without an update', async () => {
    await mountDash();
    expect(q('dashboard-update-banner')).toBeNull();
  });

  it('a stale dashboard id in the URL moves to a visible one', async () => {
    const { router } = await mountDash(world(), '/dashboards/99');
    expect(router.currentRoute.value.path).toBe('/dashboards/1');
  });

  it('with no dashboards it says so and offers to create one', async () => {
    await mountDash(world({ list: { assigned: [], mine: [], sharedReports: [] } }));
    expect(q('dashboards-none')).not.toBeNull();
    expect(qa('dashboard-create')).toHaveLength(1);
  });

  it('create sends the name and opens the new dashboard', async () => {
    const w = world({ extra: { 'POST /dashboards': detail(7, { name: '新的', kind: 'mine' }) } });
    w.details[7] = detail(7, { name: '新的', kind: 'mine' });
    const { calls } = await mountDash(w);
    await click('dashboard-create');
    const input = document.querySelector('[data-testid="dashboard-new-name"]') as HTMLInputElement;
    input.value = '新的';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('dashboard-new-confirm');
    expect(calls.find((c) => c.method === 'POST' && c.url === '/dashboards')!.body).toMatchObject({ name: '新的' });
  });

  it('delete asks to confirm', async () => {
    const { calls } = await mountDash();
    await click('dashboard-delete-3');
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await click('dashboard-delete-confirm');
    expect(calls.some((c) => c.method === 'DELETE' && c.url === '/dashboards/3')).toBe(true);
  });
});
