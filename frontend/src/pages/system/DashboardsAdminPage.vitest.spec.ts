import { flushPromises } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { systemLinks } from 'src/domain/dynamic/menu-nav';
import { card, click, mountPage, meOf, okResult, q, type World } from '../dashboards/dashboard-test-kit';
import DashboardsAdminPage from './DashboardsAdminPage.vue';

const ITEMS = [
  { id: 1, name: '業務儀表板', version: 2, sequence: 1, groupIds: [10], cardCount: 2 },
  { id: 2, name: '品質儀表板', version: 1, sequence: 2, groupIds: [], cardCount: 1 },
  { id: 3, name: '設備儀表板', version: 1, sequence: 3, groupIds: [11], cardCount: 1 },
];
const DETAIL = { id: 1, name: '業務儀表板', version: 2, sequence: 1, groupIds: [10], layout: { cards: [card('c1', 5)] } };
const world = (extra: World['extra'] = {}): World => ({
  me: meOf({ admin: true }),
  extra: {
    'GET /admin/dashboards': ITEMS,
    'GET /admin/dashboards/1': DETAIL,
    'GET /admin/groups': [
      { id: 10, key: 'sales-manager', nameI18n: { 'zh-TW': '業務主管' }, memberUserIds: [] },
      { id: 11, key: 'maint', nameI18n: { 'zh-TW': '保養' }, memberUserIds: [] },
    ],
    'GET /reports': [{ id: 5, name: '訂單狀態', ownerName: 'a', isOwner: true, sharedGroupIds: [] }],
    ...extra,
  },
});
const mountAdmin = (w: World = world()) => mountPage(w, { page: DashboardsAdminPage, route: '/system/dashboards' });

afterEach(() => { document.body.innerHTML = ''; });

describe('DashboardsAdminPage', () => {
  it('only system admins get the menu entry', () => {
    const base = { designer: { read: false, add: false, edit: false, delete: false } };
    expect(systemLinks({ isSystemAdmin: false, ...base }).some((l) => l.testid === 'dashboards-admin-menu')).toBe(false);
    expect(systemLinks({ isSystemAdmin: true, ...base }).some((l) => l.testid === 'dashboards-admin-menu')).toBe(true);
  });

  it('shows a forbidden notice for non-admins instead of the list', async () => {
    await mountAdmin(world({ 'GET /admin/dashboards': { __status: 403 } }));
    expect(q('admin-dashboard-list')).toBeNull();
  });

  it('lists the assigned dashboards in order with a summary', async () => {
    await mountAdmin();
    const rows = [...document.querySelectorAll('[data-testid="admin-dashboard-list"] > .q-item')].map((e) => e.textContent);
    expect(rows[0]).toContain('業務儀表板');
    expect(rows[1]).toContain('品質儀表板');
    expect(rows[2]).toContain('設備儀表板');
  });

  it('assign sends the checked group ids', async () => {
    const { calls } = await mountAdmin(world({ 'PUT /admin/dashboards/1/assignments': DETAIL }));
    await click('admin-dashboard-1');
    expect(q('admin-dashboard-editor')).not.toBeNull();
    // 已勾業務主管(10)；再勾保養(11)
    const boxes = [...document.querySelectorAll('[data-testid="admin-groups"] .q-checkbox')] as HTMLElement[];
    boxes[1].click();
    await flushPromises();
    await click('admin-save-assign');
    const put = calls.find((c) => c.method === 'PUT' && c.url === '/admin/dashboards/1/assignments');
    expect((put!.body as { groupIds: number[] }).groupIds.sort()).toEqual([10, 11]);
  });

  it('reorder sends the full new order', async () => {
    const { calls } = await mountAdmin(world({ 'PUT /admin/dashboards/order': ITEMS }));
    await click('admin-down-1');
    const put = calls.find((c) => c.method === 'PUT' && c.url === '/admin/dashboards/order');
    expect(JSON.stringify(put!.body)).toContain('[2,1,3]');
  });

  it('the first cannot move up and the last cannot move down', async () => {
    await mountAdmin();
    expect(q('admin-up-1')!.hasAttribute('disabled') || q('admin-up-1')!.classList.contains('disabled')).toBe(true);
    expect(q('admin-down-3')!.hasAttribute('disabled') || q('admin-down-3')!.classList.contains('disabled')).toBe(true);
  });

  it('saving the layout sends the version; a stale version shows the conflict message', async () => {
    const { calls } = await mountAdmin(world({ 'PUT /admin/dashboards/1': { __status: 409 } }));
    await click('admin-dashboard-1');
    await click('admin-save-layout');
    const put = calls.find((c) => c.method === 'PUT' && c.url === '/admin/dashboards/1');
    expect(put!.body).toMatchObject({ version: 2, name: '業務儀表板' });
    expect(q('admin-error')!.textContent).toContain('剛被其他管理員更新');
  });

  it('delete asks to confirm before sending', async () => {
    const { calls } = await mountAdmin(world({ 'DELETE /admin/dashboards/1': null }));
    await click('admin-dashboard-1');
    await click('admin-delete');
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await click('admin-delete-confirm');
    expect(calls.some((c) => c.method === 'DELETE' && c.url === '/admin/dashboards/1')).toBe(true);
  });

  it('warns when a dashboard is assigned to no group', async () => {
    await mountAdmin(world({ 'GET /admin/dashboards/1': { ...DETAIL, groupIds: [] } }));
    await click('admin-dashboard-1');
    expect(q('admin-unassigned')).not.toBeNull();
  });
  describe('卡片預覽（真實數字，用管理員自己的權限）', () => {
    const PREVIEW = 'POST /admin/dashboards/preview-card';

    it('每張卡片用編輯中的卡片設定向後端要預覽，畫面顯示真實數字與說明', async () => {
      const { calls } = await mountAdmin(world({ [PREVIEW]: okResult() }));
      await click('admin-dashboard-1');
      await flushPromises();
      const post = calls.find((c) => c.method === 'POST' && c.url === '/admin/dashboards/preview-card');
      expect(post!.body).toMatchObject({ card: { id: 'c1', reportId: 5, viz: 'table' } });
      expect(q('admin-card-c1')!.textContent).toContain('進行中');
      expect(q('admin-preview-notice')!.textContent).toContain('你自己的權限');
    });

    it('預覽沒有權限或看不到定義的卡片顯示「無法預覽」，不顯示任何數字', async () => {
      await mountAdmin(world({ [PREVIEW]: okResult({ state: 'unshared', message: '', groups: undefined, total: undefined }) }));
      await click('admin-dashboard-1');
      await flushPromises();
      expect(q('admin-preview-unavailable-c1')!.textContent).toContain('無法預覽');
      expect(q('admin-card-c1')!.textContent).not.toContain('進行中');
    });

    it('預覽失敗也是「無法預覽」，不影響移除與儲存', async () => {
      const { calls } = await mountAdmin(world({ [PREVIEW]: { __status: 500 }, 'PUT /admin/dashboards/1': DETAIL }));
      await click('admin-dashboard-1');
      await flushPromises();
      expect(q('admin-preview-unavailable-c1')).not.toBeNull();
      await click('admin-save-layout');
      expect(calls.some((c) => c.method === 'PUT' && c.url === '/admin/dashboards/1')).toBe(true);
    });
  });
});
