import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import type { Department } from 'src/domain/dynamic/organization';
import type { AdminUser } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import OrganizationPage from './OrganizationPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getDepartments: vi.fn(), createDepartment: vi.fn(), updateDepartment: vi.fn(), deleteDepartment: vi.fn(),
    getAdminUsers: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const dept = (id: number, code: string, name: string, parentId: number | null, extra: Partial<Department> = {}): Department => ({
  id, code, name: { 'zh-TW': name }, parentId, managerUserId: null, sortOrder: 0, active: true, memberUserIds: [], ...extra,
});
// 總經理室 → 業務部 → 業務一課；董事會是第二個樹根
const DEPARTMENTS = [
  dept(1, 'exec', '總經理室', null, { managerUserId: 'manager01', memberUserIds: ['manager01'] }),
  dept(2, 'sales', '業務部', 1, { memberUserIds: ['sales01'] }),
  dept(3, 'sales-1', '業務一課', 2),
  dept(4, 'board', '董事會', null, { sortOrder: 1 }),
];
const user = (userId: string, isActive = true): AdminUser => ({
  userId, displayName: `名-${userId}`, isActive, isSystemAdmin: false, isDemoAccount: false, isLocked: false, groupIds: [],
});

let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;

async function mountPage(width = 1366, usersPending = false) {
  window.innerWidth = width;
  setActivePinia(createPinia());
  api.getDepartments.mockResolvedValue(DEPARTMENTS);
  api.getAdminUsers.mockImplementation(() => (usersPending ? new Promise(() => {}) : Promise.resolve([user('manager01'), user('sales01'), user('old01', false)])));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(OrganizationPage))) };
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n,
        [VueQueryPlugin, { queryClient }]],
    },
  });
  await flushPromises();
  // Quasar's Screen listens to resize (debounced); jsdom starts at 1024 wide
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  mounted = w;
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
const settled = async () => { await new Promise((r) => setTimeout(r, 400)); await flushPromises(); };

describe('OrganizationPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('shows no action buttons until the permission check (admin users request) has finished', async () => {
    await mountPage(1366, true);
    expect(q('dept-add')).toBeNull();
    expect(q('dept-node-1')).toBeNull();
  });

  it('shows the tree in order with indentation by depth', async () => {
    await mountPage();
    const nodes = [...document.body.querySelectorAll('[data-testid^="dept-node-"]')].map((e) => e.getAttribute('data-testid'));
    expect(nodes).toEqual(['dept-node-1', 'dept-node-2', 'dept-node-3', 'dept-node-4']);
    expect(q('dept-node-3')!.getAttribute('data-depth')).toBe('2');
  });

  it('desktop: clicking a department shows its detail beside the tree, with manager and members', async () => {
    await mountPage();
    await click('dept-node-1');
    expect(q('dept-dialog')).toBeNull();
    expect(q('dept-detail')!.textContent).toContain('名-manager01');
    expect(q('dept-members')!.textContent).toContain('名-manager01');
  });

  it('390 wide: clicking a department opens a full-screen detail', async () => {
    await mountPage(390);
    await click('dept-node-2');
    await settled();
    expect(q('dept-dialog')).not.toBeNull();
    expect(q('dept-detail')).not.toBeNull();
  });

  it('"Move to…" leaves out the department itself and its descendants, then saves the new parent', async () => {
    api.updateDepartment.mockResolvedValue(DEPARTMENTS[0]);
    await mountPage();
    await click('dept-node-1');
    await click('dept-move');
    await settled();
    expect(q('move-option-4')).not.toBeNull();
    for (const id of [1, 2, 3]) expect(q(`move-option-${id}`)).toBeNull();
    expect(q('move-option-root')).toBeNull();   // already a root
    await click('move-option-4');
    expect(api.updateDepartment).toHaveBeenCalledWith(1, expect.objectContaining({ code: 'exec', parentId: 4, managerUserId: 'manager01' }));
  });

  it('a child department can be moved up to root', async () => {
    api.updateDepartment.mockResolvedValue(DEPARTMENTS[2]);
    await mountPage();
    await click('dept-node-3');
    await click('dept-move');
    await settled();
    expect(q('move-option-3')).toBeNull();
    expect(q('move-option-1')).not.toBeNull();
    await click('move-option-root');
    expect(api.updateDepartment).toHaveBeenCalledWith(3, expect.objectContaining({ parentId: null }));
  });

  it('delete is blocked with a reason while the department has children or members', async () => {
    await mountPage();
    await click('dept-node-2');
    expect((q('dept-delete') as HTMLButtonElement).disabled).toBe(true);
    expect(q('dept-delete-blocked')!.textContent).toContain('1');
  });

  it('add child creates a department under the selected one', async () => {
    api.createDepartment.mockResolvedValue(dept(9, 'sales-2', '業務二課', 2));
    await mountPage();
    await click('dept-node-2');
    await click('dept-add-child');
    const code = q('dept-code') as HTMLInputElement;
    code.value = 'sales-2';
    code.dispatchEvent(new Event('input'));
    const zh = q('i18n-zh-TW')!.querySelector('input')!;
    zh.value = '業務二課';
    zh.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('dept-save');
    expect(api.createDepartment).toHaveBeenCalledWith({
      code: 'sales-2', name: { 'zh-TW': '業務二課' }, parentId: 2, managerUserId: null, sortOrder: 0, active: true,
    });
  });
});
