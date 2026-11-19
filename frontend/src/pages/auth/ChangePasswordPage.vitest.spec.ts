import { onlineManager, QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import ChangePasswordPage from './ChangePasswordPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { changePassword: vi.fn(), logout: vi.fn() } }));
const api = vi.mocked(dynamicApi);

let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;

async function mountPage(forced = true) {
  setActivePinia(createPinia());
  useIdentityStore().setSession('admin', forced);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/change-password', component: ChangePasswordPage }, { path: '/login', component: { template: '<i/>' } }, { path: '/', component: { template: '<i/>' } }],
  });
  await router.push('/change-password');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  queryClient = new QueryClient();
  const w = mount(ChangePasswordPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router, [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
  mounted = w;
  return router;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function fill(id: string, value: string) {
  const el = q(id) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
async function submit(oldPw: string, next: string, confirm: string) {
  await fill('cp-old', oldPw);
  await fill('cp-new', next);
  await fill('cp-confirm', confirm);
  q('cp-submit')!.click();
  await flushPromises();
}

/** 共用快取（查詢與 mutation，含 variables、結果與錯誤）序列化後的全文。 */
const cacheText = () => JSON.stringify([
  queryClient.getQueryCache().getAll().map((x) => x.state),
  queryClient.getMutationCache().getAll().map((x) => x.state),
]);

describe('ChangePasswordPage', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => mounted?.unmount());

  it('does not submit when the two new passwords differ', async () => {
    await mountPage();
    await submit('old-password', 'new-password-1', 'new-password-2');
    expect(api.changePassword).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain(zhTW.auth.passwordMismatch);
  });

  it('does not submit a password shorter than 8 characters', async () => {
    await mountPage();
    await submit('old-password', 'short', 'short');
    expect(api.changePassword).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain(zhTW.auth.passwordTooShort);
  });

  it('success clears the forced flag and goes home', async () => {
    const router = await mountPage();
    api.changePassword.mockResolvedValue({ userId: 'admin', displayName: 'A', mustChangePassword: false });
    await submit('old-password', 'new-password-1', 'new-password-1');
    expect(api.changePassword).toHaveBeenCalledWith({ oldPassword: 'old-password', newPassword: 'new-password-1' });
    expect(useIdentityStore().mustChangePassword).toBe(false);
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('shows the backend 400 message and stays', async () => {
    const router = await mountPage();
    api.changePassword.mockRejectedValue({ status: 400, errors: { OldPassword: ['目前密碼不正確'] } });
    await submit('wrong-password', 'new-password-1', 'new-password-1');
    expect(q('cp-error')!.textContent).toContain('目前密碼不正確');
    expect(router.currentRoute.value.path).toBe('/change-password');
  });

  it('forced change: logout button signs out and returns to /login; no way back to the app', async () => {
    const router = await mountPage(true);
    api.logout.mockResolvedValue(undefined as never);
    expect(q('cp-back')).toBeNull();
    q('cp-logout')!.click();
    await flushPromises();
    expect(api.logout).toHaveBeenCalledTimes(1);
    expect(useIdentityStore().userId).toBe('');
    expect(router.currentRoute.value.path).toBe('/login');
  });

  it('logout failure keeps the session and the page, and shows an error', async () => {
    const router = await mountPage(true);
    api.logout.mockRejectedValue({ status: 0, message: 'Network Error' });
    q('cp-logout')!.click();
    await flushPromises();
    expect(useIdentityStore().userId).toBe('admin');
    expect(router.currentRoute.value.path).toBe('/change-password');
    expect(q('cp-error')!.textContent).toContain(zhTW.auth.logoutFailed);
  });

  it('voluntary change: offers a way back to the app', async () => {
    const router = await mountPage(false);
    expect(q('cp-required')).toBeNull();
    q('cp-back')!.click();
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/');
  });
  describe('密碼不留在共用快取', () => {
    it('改密碼成功後共用快取不含新舊密碼明文', async () => {
      await mountPage();
      api.changePassword.mockResolvedValue({ userId: 'admin', displayName: 'A', mustChangePassword: false });
      await submit('s3cret-old', 's3cret-new-1', 's3cret-new-1');
      expect(api.changePassword).toHaveBeenCalledWith({ oldPassword: 's3cret-old', newPassword: 's3cret-new-1' });
      expect(cacheText()).not.toContain('s3cret');
    });

    it('改密碼失敗後共用快取不含新舊密碼明文', async () => {
      await mountPage();
      api.changePassword.mockRejectedValue({ status: 400, errors: { OldPassword: ['目前密碼不正確'] } });
      await submit('s3cret-old', 's3cret-new-1', 's3cret-new-1');
      expect(api.changePassword).toHaveBeenCalledTimes(1);
      expect(cacheText()).not.toContain('s3cret');
    });

    it('失敗後離頁，快取裡留著的 mutation 再執行也讀不到密碼', async () => {
      await mountPage();
      api.changePassword.mockRejectedValue({ status: 400, errors: { OldPassword: ['目前密碼不正確'] } });
      await submit('s3cret-old', 's3cret-new-1', 's3cret-new-1');
      const m = queryClient.getMutationCache().getAll().at(-1)!;
      mounted!.unmount();
      mounted = undefined;
      await Promise.resolve(m.state.variables).then(m.options.mutationFn as (v: unknown) => Promise<unknown>).catch(() => undefined);
      // 綁在當次提交的密碼已取用；再執行就中止，不改送空密碼
      expect(api.changePassword).toHaveBeenCalledTimes(1);
    });

    it('離線時送出不排隊，送的是當次的新舊密碼；之後改欄位、恢復連線也不補送', async () => {
      await mountPage();
      api.changePassword.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      onlineManager.setOnline(false);
      try {
        await submit('s3cret-old', 's3cret-new-1', 's3cret-new-1');
        await fill('cp-old', 's3cret-other');
        await fill('cp-new', 's3cret-new-2');
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.changePassword.mock.calls).toEqual([[{ oldPassword: 's3cret-old', newPassword: 's3cret-new-1' }]]);
      } finally { onlineManager.setOnline(true); }
    });

    it('離線送出後離頁，恢復連線也不再送出', async () => {
      await mountPage();
      api.changePassword.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      onlineManager.setOnline(false);
      try {
        await submit('s3cret-old', 's3cret-new-1', 's3cret-new-1');
        mounted!.unmount();
        mounted = undefined;
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.changePassword).toHaveBeenCalledTimes(1);
      } finally { onlineManager.setOnline(true); }
    });
  });
});
