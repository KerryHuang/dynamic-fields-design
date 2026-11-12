import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import type { AdminUser } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import DepartmentSelect from 'src/presentation/organization/DepartmentSelect.vue';
import type { Department } from 'src/domain/dynamic/organization';
import UsersPage from './UsersPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getAdminUsers: vi.fn(), createUser: vi.fn(), updateUser: vi.fn(), resetUserPassword: vi.fn(), setUserActive: vi.fn(),
    getGroups: vi.fn(), getUsers: vi.fn(), getMe: vi.fn(), getDepartments: vi.fn(), setUserDepartment: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const user = (userId: string, extra: Partial<AdminUser> = {}): AdminUser => ({
  userId, displayName: `名-${userId}`, isActive: true, isSystemAdmin: false, isDemoAccount: false, isLocked: false, groupIds: [], ...extra,
});
const dept = (id: number, code: string, name: string, parentId: number | null): Department => ({
  id, code, name: { 'zh-TW': name }, parentId, managerUserId: null, sortOrder: 0, active: true, memberUserIds: [],
});
const DEPARTMENTS = [dept(1, 'exec', '總經理室', null), dept(2, 'sales', '業務部', 1)];
const USERS = [
  user('admin', { isSystemAdmin: true }),
  user('sales01', { groupIds: [1], departmentId: 2 }),
  user('old01', { isActive: false }),
];

let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;

async function mountPage(users: AdminUser[] | { status: number } = USERS, width = 1366) {
  window.innerWidth = width;
  setActivePinia(createPinia());
  useIdentityStore().setSession('admin');
  if (Array.isArray(users)) api.getAdminUsers.mockResolvedValue(users);
  else api.getAdminUsers.mockRejectedValue(users);
  api.getGroups.mockResolvedValue([{ id: 1, key: 'sales', name: { 'zh-TW': '業務群組' }, members: [] }]);
  api.getMe.mockResolvedValue({ userId: 'admin', isSystemAdmin: true } as never);
  api.getUsers.mockResolvedValue([]);
  api.getDepartments.mockResolvedValue(DEPARTMENTS);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(UsersPage))) };
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
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
const press = async (id: string, key: string) => {
  q(id)!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
  await flushPromises();
};
async function fill(id: string, value: string) {
  const el = q(id)!;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}

describe('UsersPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('390 wide: shows cards (not table rows) with the same fields and actions', async () => {
    await mountPage(USERS, 390);
    expect(document.body.querySelector('[data-testid^="user-row-"]')).toBeNull();
    const card = q('user-card-sales01')!;
    expect(card).not.toBeNull();
    for (const text of ['sales01', '名-sales01', '業務群組', '總經理室 / 業務部', '啟用中']) expect(card.textContent).toContain(text);
    expect(q('user-card-admin')!.textContent).toContain('系統管理員');
    expect(q('user-card-old01')!.textContent).toContain('已停用');
    await click('user-card-sales01');
    expect((q('user-id') as HTMLInputElement).value).toBe('sales01');
  });

  it('click a row opens the edit dialog; action buttons do not', async () => {
    await mountPage(USERS, 1366);
    expect(q('user-row-sales01')!.classList.contains('row-clickable')).toBe(true);
    expect(q('user-edit-sales01')).toBeNull();
    await click('user-reset-sales01');
    expect(q('user-id')).toBeNull();
    expect(q('user-reset-password')).not.toBeNull();
    await click('user-reset-cancel');
    await click('user-toggle-old01');
    expect(q('user-id')).toBeNull();
    await click('user-row-sales01');
    expect((q('user-id') as HTMLInputElement).value).toBe('sales01');
  });

  it('keyboard: the row is focusable; Enter and Space open the edit dialog, Enter on the inner button does not', async () => {
    await mountPage(USERS, 1366);
    expect(q('user-row-sales01')!.getAttribute('tabindex')).toBe('0');
    await press('user-row-sales01', 'Enter');
    expect((q('user-id') as HTMLInputElement).value).toBe('sales01');
    await click('user-cancel');
    await press('user-row-old01', ' ');
    expect((q('user-id') as HTMLInputElement).value).toBe('old01');
    await click('user-cancel');
    await press('user-reset-sales01', 'Enter');
    expect(q('user-id')).toBeNull();
  });

  it('390 wide keyboard: the card is focusable and Enter opens the edit dialog', async () => {
    await mountPage(USERS, 390);
    expect(q('user-card-sales01')!.getAttribute('tabindex')).toBe('0');
    await press('user-card-sales01', 'Enter');
    expect((q('user-id') as HTMLInputElement).value).toBe('sales01');
  });

  it('390 wide: action buttons on a card do not open the edit dialog', async () => {
    await mountPage(USERS, 390);
    await click('user-reset-sales01');
    expect(q('user-id')).toBeNull();
    expect(q('user-reset-password')).not.toBeNull();
  });

  it('wide screens keep the table', async () => {
    await mountPage(USERS, 1366);
    expect(q('user-row-sales01')).not.toBeNull();
    expect(q('user-card-sales01')).toBeNull();
  });

  it('lists accounts with name, group names, status and admin flag, inactive ones included', async () => {
    await mountPage();
    const text = (id: string) => q(`user-row-${id}`)!.textContent!;
    expect(text('admin')).toContain('名-admin');
    expect(text('admin')).toContain(zhTW.users.systemAdmin);
    expect(text('sales01')).toContain('業務群組');
    expect(text('old01')).toContain(zhTW.users.inactive);
  });

  it('shows the department path column and filters by department', async () => {
    const w = await mountPage();
    expect(q('user-row-sales01')!.textContent).toContain('總經理室 / 業務部');
    const filter = w.findAllComponents(DepartmentSelect).find((c) => c.props('label') === zhTW.users.filterDepartment)!;
    filter.vm.$emit('update:modelValue', 2);
    await flushPromises();
    expect(q('user-row-sales01')).not.toBeNull();
    expect(q('user-row-admin')).toBeNull();
    filter.vm.$emit('update:modelValue', null);
    await flushPromises();
    expect(q('user-row-admin')).not.toBeNull();
  });

  it('choosing a department in the edit dialog sends PUT admin/users/{id}/department', async () => {
    api.updateUser.mockResolvedValue(user('sales01'));
    api.setUserDepartment.mockResolvedValue(undefined as never);
    const w = await mountPage();
    await click('user-row-sales01');
    const select = w.findAllComponents(DepartmentSelect).find((c) => c.props('label') === zhTW.users.department)!;
    expect(select.props('modelValue')).toBe(2);
    select.vm.$emit('update:modelValue', 1);
    await flushPromises();
    await click('user-save');
    expect(api.setUserDepartment).toHaveBeenCalledWith('sales01', 1);
  });

  it('saving without changing the department does not call the department endpoint', async () => {
    api.updateUser.mockResolvedValue(user('sales01'));
    await mountPage();
    await click('user-row-sales01');
    await click('user-save');
    expect(api.updateUser).toHaveBeenCalled();
    expect(api.setUserDepartment).not.toHaveBeenCalled();
  });

  it('create sends the exact body', async () => {
    api.createUser.mockResolvedValue(user('new01'));
    await mountPage();
    await click('user-create');
    await fill('user-id', 'new01');
    await fill('user-name', '新人');
    await fill('user-password', 'password-1');
    await click('user-save');
    expect(api.createUser).toHaveBeenCalledWith({ userId: 'new01', displayName: '新人', password: 'password-1', groupIds: [], isSystemAdmin: false });
  });

  it.each([
    [{ status: 409, message: '帳號已存在' }, '帳號已存在'],
    [{ status: 400, errors: { Password: ['密碼至少 8 碼'] } }, '密碼至少 8 碼'],
  ])('create failure %j is shown in the dialog', async (err, shown) => {
    api.createUser.mockRejectedValue(err);
    await mountPage();
    await click('user-create');
    await fill('user-id', 'new01');
    await fill('user-name', '新人');
    await fill('user-password', 'password-1');
    await click('user-save');
    expect(q('user-error')!.textContent).toContain(shown);
    expect(q('user-save')).not.toBeNull();
  });

  it('edit keeps the account read-only and sends a full update including groupIds', async () => {
    api.updateUser.mockResolvedValue(user('sales01'));
    await mountPage();
    await click('user-row-sales01');
    expect(q('user-id')!.readOnly).toBe(true);
    expect(q('user-id')!.value).toBe('sales01');
    expect(q('user-password')).toBeNull();
    await fill('user-name', '改名');
    await click('user-save');
    expect(api.updateUser).toHaveBeenCalledWith('sales01', { displayName: '改名', groupIds: [1], isSystemAdmin: false });
  });

  it('reset password refuses fewer than 8 characters, then notifies on success', async () => {
    api.resetUserPassword.mockResolvedValue(undefined as never);
    const w = await mountPage();
    const notify = vi.spyOn(w.vm.$q, 'notify');
    await click('user-reset-sales01');
    await fill('user-reset-password', '1234567');
    await click('user-reset-submit');
    expect(api.resetUserPassword).not.toHaveBeenCalled();
    await fill('user-reset-password', '12345678');
    await click('user-reset-submit');
    expect(api.resetUserPassword).toHaveBeenCalledWith('sales01', '12345678');
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive', message: zhTW.users.passwordReset }));
  });

  it('own deactivate button is disabled; deactivating someone else asks first', async () => {
    api.setUserActive.mockResolvedValue(undefined as never);
    await mountPage();
    expect(q('user-toggle-admin')!.disabled).toBe(true);
    await click('user-toggle-sales01');
    expect(api.setUserActive).not.toHaveBeenCalled();
    await click('user-confirm-ok');
    expect(api.setUserActive).toHaveBeenCalledWith('sales01', false);
  });

  it('activating an inactive user needs no confirmation', async () => {
    api.setUserActive.mockResolvedValue(undefined as never);
    await mountPage();
    await click('user-toggle-old01');
    expect(api.setUserActive).toHaveBeenCalledWith('old01', true);
  });

  it('shows ForbiddenNotice on 403 and does not refetch in a loop', async () => {
    await mountPage({ status: 403 });
    expect(q('forbidden')).not.toBeNull();
    expect(q('user-create')).toBeNull();
    expect(api.getAdminUsers).toHaveBeenCalledTimes(1);
  });

  it('after a mutation, invalidates the list, users and me', async () => {
    api.updateUser.mockResolvedValue(user('admin'));
    await mountPage();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    await click('user-row-admin');
    await click('user-admin');
    await click('user-save');
    expect(api.updateUser).toHaveBeenCalledWith('admin', { displayName: '名-admin', groupIds: [], isSystemAdmin: false });
    expect(invalidate.mock.calls.map(([f]) => (f as { queryKey: unknown[] }).queryKey)).toEqual(expect.arrayContaining([['admin-users'], ['users'], ['me']]));
  });

  describe('passwords do not outlive their dialog', () => {
    const state = (w: Awaited<ReturnType<typeof mountPage>>) =>
      w.findComponent(UsersPage).vm as unknown as { form: { password: string }; resetPassword: string };
    const settled = async () => { await new Promise((r) => setTimeout(r, 400)); await flushPromises(); };

    it('create dialog: cleared after cancel and after a successful save', async () => {
      api.createUser.mockResolvedValue(user('new01'));
      const w = await mountPage();
      await click('user-create');
      await fill('user-password', 'password-1');
      expect(state(w).form.password).toBe('password-1');
      await click('user-cancel');
      await settled();
      expect(state(w).form.password).toBe('');

      await click('user-create');
      await fill('user-id', 'new01');
      await fill('user-name', '新人');
      await fill('user-password', 'password-2');
      await click('user-save');
      await settled();
      expect(api.createUser).toHaveBeenCalledTimes(1);
      expect(state(w).form.password).toBe('');
    });

    it('reset dialog: cleared after cancel, after the dialog asks to close (backdrop/Escape) and after success', async () => {
      api.resetUserPassword.mockResolvedValue(undefined as never);
      const w = await mountPage();
      await click('user-reset-sales01');
      await fill('user-reset-password', 'password-1');
      await click('user-reset-cancel');
      await settled();
      expect(state(w).resetPassword).toBe('');

      await click('user-reset-sales01');
      await fill('user-reset-password', 'password-2');
      // Backdrop click and Escape both end in QDialog's update:model-value(false)
      w.findAllComponents(quasar.QDialog).find((d) => d.props('modelValue'))!.vm.$emit('update:model-value', false);
      await settled();
      expect(state(w).resetPassword).toBe('');

      await click('user-reset-sales01');
      await fill('user-reset-password', 'password-3');
      await click('user-reset-submit');
      await settled();
      expect(api.resetUserPassword).toHaveBeenCalledTimes(1);
      expect(state(w).resetPassword).toBe('');
    });
  });
});
