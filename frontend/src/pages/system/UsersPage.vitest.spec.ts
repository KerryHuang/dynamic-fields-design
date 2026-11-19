import { onlineManager, QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import type { AdminUser, SystemPrivileges } from 'src/domain/dynamic/types';
import { noSystemPrivileges } from 'src/domain/dynamic/system-privileges';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import DepartmentSelect from 'src/presentation/organization/DepartmentSelect.vue';
import type { Department } from 'src/domain/dynamic/organization';
import UsersPage from './UsersPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getAdminUsers: vi.fn(), createUser: vi.fn(), updateUser: vi.fn(), resetUserPassword: vi.fn(), setUserActive: vi.fn(),
    getGroups: vi.fn(), getUsers: vi.fn(), getMe: vi.fn(), getDepartments: vi.fn(), setUserDepartment: vi.fn(), setUserSystemPrivileges: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const user = (userId: string, extra: Partial<AdminUser> = {}): AdminUser => ({
  userId, displayName: `名-${userId}`, isActive: true, isSystemAdmin: false, isDemoAccount: false, isLocked: false, groupIds: [],
  systemPrivileges: noSystemPrivileges(), effectiveSystemPrivileges: noSystemPrivileges(), systemPrivilegeSources: [], ...extra,
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

/** 共用快取（查詢與 mutation，含 variables、結果與錯誤）序列化後的全文。 */
const cacheText = () => JSON.stringify([
  queryClient.getQueryCache().getAll().map((x) => x.state),
  queryClient.getMutationCache().getAll().map((x) => x.state),
]);
/** 離頁後把快取裡留著的 mutation 用它的 variables 再跑一次（模擬 MutationCache 保留的 mutationFn 閉包被叫用）。 */
async function rerunLeftover() {
  const m = queryClient.getMutationCache().getAll().at(-1)!;
  mounted!.unmount();
  mounted = undefined;
  await Promise.resolve(m.state.variables).then(m.options.mutationFn as (v: unknown) => Promise<unknown>).catch(() => undefined);
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
  describe('passwords stay out of the shared cache', () => {
    /** offline：頁面載入後才斷線（查詢在離線時不會載入）。 */
    async function openReset(pw: string, offline = false) {
      await mountPage();
      if (offline) onlineManager.setOnline(false);
      await click('user-reset-sales01');
      await fill('user-reset-password', pw);
      await click('user-reset-submit');
    }
    async function createWith(pw: string, offline = false) {
      await mountPage();
      if (offline) onlineManager.setOnline(false);
      await click('user-create');
      await fill('user-id', 'new01');
      await fill('user-name', '新人');
      await fill('user-password', pw);
      await click('user-save');
    }

    it('重設密碼成功與失敗後，共用快取都不含明文', async () => {
      api.resetUserPassword.mockResolvedValue(undefined as never);
      await openReset('s3cret-ok-1');
      expect(api.resetUserPassword).toHaveBeenCalledWith('sales01', 's3cret-ok-1');
      expect(cacheText()).not.toContain('s3cret-ok-1');
      mounted!.unmount();
      document.body.innerHTML = '';
      api.resetUserPassword.mockRejectedValue({ status: 500, message: 'boom' });
      await openReset('s3cret-fail-1');
      expect(api.resetUserPassword).toHaveBeenLastCalledWith('sales01', 's3cret-fail-1');
      expect(cacheText()).not.toContain('s3cret-fail-1');
    });

    it('重設密碼失敗後離頁，快取裡留著的 mutation 再執行也讀不到密碼', async () => {
      api.resetUserPassword.mockRejectedValue({ status: 500, message: 'boom' });
      await openReset('s3cret-left-1');
      await rerunLeftover();
      // 綁在當次提交的密碼已取用；再執行就中止，不改送空密碼
      expect(api.resetUserPassword).toHaveBeenCalledTimes(1);
    });

    it('離線時重設不排隊：送出 A 後關對話框、改開 B 輸入密碼，恢復連線也不會把 B 的密碼送給 A', async () => {
      api.resetUserPassword.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      try {
        await openReset('s3cret-for-a', true);
        await click('user-reset-cancel');
        await click('user-reset-old01');
        await fill('user-reset-password', 's3cret-for-b');
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.resetUserPassword.mock.calls).toEqual([['sales01', 's3cret-for-a']]);
      } finally { onlineManager.setOnline(true); }
    });

    it('離線重設送出後離頁，恢復連線也不再送出', async () => {
      api.resetUserPassword.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      try {
        await openReset('s3cret-gone-1', true);
        mounted!.unmount();
        mounted = undefined;
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.resetUserPassword).toHaveBeenCalledTimes(1);
      } finally { onlineManager.setOnline(true); }
    });

    it('建立帳號成功與失敗後，共用快取都不含初始密碼明文', async () => {
      api.createUser.mockResolvedValue(user('new01'));
      await createWith('s3cret-ok-2');
      expect(api.createUser).toHaveBeenCalledWith(expect.objectContaining({ password: 's3cret-ok-2' }));
      expect(cacheText()).not.toContain('s3cret-ok-2');
      mounted!.unmount();
      document.body.innerHTML = '';
      api.createUser.mockRejectedValue({ status: 409, message: '帳號已存在' });
      await createWith('s3cret-fail-2');
      expect(api.createUser).toHaveBeenLastCalledWith(expect.objectContaining({ password: 's3cret-fail-2' }));
      expect(cacheText()).not.toContain('s3cret-fail-2');
    });

    it('建立帳號失敗後離頁，快取裡留著的 mutation 再執行也讀不到密碼', async () => {
      api.createUser.mockRejectedValue({ status: 409, message: '帳號已存在' });
      await createWith('s3cret-left-2');
      await rerunLeftover();
      expect(api.createUser).toHaveBeenCalledTimes(1);
    });

    it('離線時建立帳號不排隊：送的是當次的初始密碼，之後改欄位、恢復連線也不補送', async () => {
      api.createUser.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      try {
        await createWith('s3cret-first-2', true);
        await fill('user-password', 's3cret-second-2');
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.createUser.mock.calls).toEqual([[expect.objectContaining({ userId: 'new01', password: 's3cret-first-2' })]]);
      } finally { onlineManager.setOnline(true); }
    });
  });

  describe('系統權限（權限軸-b）', () => {
    const sp = (designer: Partial<SystemPrivileges['designer']>, permissionAdmin = false): SystemPrivileges =>
      ({ designer: { ...noSystemPrivileges().designer, ...designer }, permissionAdmin });
    // 只經群組 IT 取得設計器讀取＋修改與權限設定，個人只有設計器新增
    const wang = user('wang', {
      groupIds: [1], systemPrivileges: sp({ add: true }), effectiveSystemPrivileges: sp({ read: true, add: true, edit: true }, true),
      systemPrivilegeSources: [{ kind: 'group', groupId: 1, groupName: 'IT', designer: { read: true, add: false, edit: true, delete: false }, permissionAdmin: true }],
    });
    const boss = user('boss', {
      isSystemAdmin: true, effectiveSystemPrivileges: sp({ read: true, add: true, edit: true, delete: true }, true), systemPrivilegeSources: [{ kind: 'systemAdmin' }],
    });
    const LIST = [...USERS, wang, boss];
    const checked = (id: string) => q(id)!.getAttribute('aria-checked');

    it('PAX-31 載入個人直接設定、另顯示經群組取得的唯讀一行；存檔送 PUT admin/users/{id}/system-privileges，body 只含直接設定', async () => {
      api.updateUser.mockResolvedValue(wang);
      api.setUserSystemPrivileges.mockResolvedValue(sp({ add: true, delete: true }));
      await mountPage(LIST);
      await click('user-row-wang');
      expect(checked('sp-designer-read')).toBe('false');
      expect(checked('sp-designer-add')).toBe('true');
      expect(checked('sp-permission-admin')).toBe('false');
      expect(q('system-privileges-inherited')!.textContent).toBe('另經群組取得：設計器（讀取、修改）來自『IT』；權限設定來自『IT』');
      expect(q('system-privileges-admin-hint')).toBeNull();
      await click('sp-designer-delete');
      await click('user-save');
      expect(api.updateUser).toHaveBeenCalledWith('wang', { displayName: '名-wang', groupIds: [1], isSystemAdmin: false });
      expect(api.setUserSystemPrivileges).toHaveBeenCalledWith('wang', sp({ add: true, delete: true }));
    });

    it('PAX-31 回歸：只經群組取得權限設定的人，存檔不把繼承的存成個人（送出的 permissionAdmin 是 false）', async () => {
      api.updateUser.mockResolvedValue(wang);
      api.setUserSystemPrivileges.mockResolvedValue(sp({ read: true, add: true }));
      await mountPage(LIST);
      await click('user-row-wang');
      await click('user-save');
      // 不改系統權限：只存帳號，不送系統權限（不多一筆稽核）
      expect(api.updateUser).toHaveBeenCalledTimes(1);
      expect(api.setUserSystemPrivileges).not.toHaveBeenCalled();
      await click('user-row-wang');
      await click('sp-designer-read');
      await click('user-save');
      const body = api.setUserSystemPrivileges.mock.calls[0]![1];
      expect(body.permissionAdmin).toBe(false);
      expect(body.designer).toEqual({ read: true, add: true, edit: false, delete: false });
    });

    it('PAX-31 目標是系統管理員時顯示自動擁有的提示，區塊照常可編', async () => {
      await mountPage(LIST);
      await click('user-row-boss');
      expect(q('system-privileges-admin-hint')!.textContent).toBe('系統管理員自動擁有設計器與權限設定；以下設定在取消系統管理員後才生效');
      expect(q('sp-designer-read')!.getAttribute('aria-disabled')).not.toBe('true');
      // 系統管理員那筆來源不另列成「經群組取得」
      expect(q('system-privileges-inherited')).toBeNull();
      await click('user-cancel');
      await click('user-row-sales01');
      expect(q('system-privileges-admin-hint')).toBeNull();
      await click('user-admin');
      expect(q('system-privileges-admin-hint')).not.toBeNull();
    });

    it('PAX-31 第二個請求失敗：帳號已存、對話框保持開啟並提示系統權限未儲存', async () => {
      api.updateUser.mockResolvedValue(wang);
      api.setUserSystemPrivileges.mockRejectedValue({ status: 500, message: '伺服器錯誤' });
      await mountPage(LIST);
      await click('user-row-wang');
      await click('sp-permission-admin');
      await click('user-save');
      expect(api.updateUser).toHaveBeenCalledTimes(1);
      expect(q('user-error')!.textContent).toBe('名稱與成員已儲存，系統權限未儲存：伺服器錯誤');
      expect(q('user-save')).not.toBeNull();
    });

    it('新增使用者沒有系統權限區塊', async () => {
      await mountPage(LIST);
      await click('user-create');
      expect(q('system-privileges')).toBeNull();
    });

    it('PAX-33 使用者清單以有效結果顯示「設計器」「權限設定」標籤（桌機表格）', async () => {
      await mountPage(LIST);
      expect(q('user-sp-wang')!.textContent).toContain('設計器');
      expect(q('user-sp-wang')!.textContent).toContain('權限設定');
      expect(q('user-sp-boss')!.textContent).toContain('權限設定');
      expect(q('user-sp-sales01')!.textContent!.trim()).toBe('');
    });

    it('PAX-33 390 寬的卡片同樣顯示有效結果的標籤', async () => {
      await mountPage(LIST, 390);
      expect(q('user-card-wang')!.textContent).toContain('系統權限');
      expect(q('user-sp-wang')!.textContent).toContain('設計器');
      expect(q('user-sp-wang')!.textContent).toContain('權限設定');
      expect(q('user-card-sales01')!.textContent).not.toContain('系統權限');
    });
  });
});
