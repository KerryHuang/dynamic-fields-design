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
import UsersPage from './UsersPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getAdminUsers: vi.fn(), createUser: vi.fn(), updateUser: vi.fn(), resetUserPassword: vi.fn(), setUserActive: vi.fn(),
    getGroups: vi.fn(), getUsers: vi.fn(), getMe: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const user = (userId: string, extra: Partial<AdminUser> = {}): AdminUser => ({
  userId, displayName: `名-${userId}`, isActive: true, isSystemAdmin: false, isDemoAccount: false, isLocked: false, groupIds: [], ...extra,
});
const USERS = [
  user('admin', { isSystemAdmin: true }),
  user('sales01', { groupIds: [1] }),
  user('old01', { isActive: false }),
];

let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;

async function mountPage(users: AdminUser[] | { status: number } = USERS) {
  setActivePinia(createPinia());
  useIdentityStore().setSession('admin');
  if (Array.isArray(users)) api.getAdminUsers.mockResolvedValue(users);
  else api.getAdminUsers.mockRejectedValue(users);
  api.getGroups.mockResolvedValue([{ id: 1, key: 'sales', name: { 'zh-TW': '業務群組' }, members: [] }]);
  api.getMe.mockResolvedValue({ userId: 'admin', isSystemAdmin: true } as never);
  api.getUsers.mockResolvedValue([]);
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
  mounted = w;
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
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

  it('lists accounts with name, group names, status and admin flag, inactive ones included', async () => {
    await mountPage();
    const text = (id: string) => q(`user-row-${id}`)!.textContent!;
    expect(text('admin')).toContain('名-admin');
    expect(text('admin')).toContain(zhTW.users.systemAdmin);
    expect(text('sales01')).toContain('業務群組');
    expect(text('old01')).toContain(zhTW.users.inactive);
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
    await click('user-edit-sales01');
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
    await click('user-edit-admin');
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
