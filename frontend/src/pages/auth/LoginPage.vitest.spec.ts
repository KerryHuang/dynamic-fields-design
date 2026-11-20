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
import { resetSessionSettings } from 'src/application/dynamic/session-settings';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import { installAuthGuard } from 'src/router/auth-guard';
import LoginPage from './LoginPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getAuthOptions: vi.fn(), login: vi.fn(), demoLogin: vi.fn(), getMe: vi.fn(), getEffectivePolicies: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

let mounted: ReturnType<typeof mount> | undefined;

async function mountPage(options: unknown, redirect?: string) {
  setActivePinia(createPinia());
  api.getAuthOptions.mockResolvedValue(options as never);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/login', component: LoginPage }, { path: '/:p(.*)*', component: { template: '<i/>' } }],
  });
  await router.push(redirect ? { path: '/login', query: { redirect } } : '/login');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(['stale'], 1);
  const w = mount(LoginPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router, [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
  mounted = w;
  return { w, router, queryClient };
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function fill(id: string, value: string) {
  const el = q(id) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
async function submit(user = 'Admin', pw = 'secret-pw') {
  await fill('login-user', user);
  await fill('login-password', pw);
  q('login-submit')!.click();
  await flushPromises();
}

/** 共用快取（查詢與 mutation，含 variables、結果與錯誤）序列化後的全文。 */
const cacheText = (qc: QueryClient) => JSON.stringify([
  qc.getQueryCache().getAll().map((x) => x.state),
  qc.getMutationCache().getAll().map((x) => x.state),
]);
/** 離頁後把快取裡留著的 mutation 用它的 variables 再跑一次（模擬 MutationCache 保留的 mutationFn 閉包被叫用）。 */
async function rerunLeftover(qc: QueryClient) {
  const m = qc.getMutationCache().getAll().at(-1)!;
  mounted!.unmount();
  mounted = undefined;
  await Promise.resolve(m.state.variables).then(m.options.mutationFn as (v: unknown) => Promise<unknown>).catch(() => undefined);
}

const off = { demoLoginEnabled: false, demoGenerating: false, demoAccounts: [] };
const on = { demoLoginEnabled: true, demoGenerating: false, demoAccounts: [{ userId: 'sales01', displayName: '業務一' }, { userId: 'floor01', displayName: '現場一' }] };

describe('LoginPage', () => {
  beforeEach(() => { vi.clearAllMocks(); });
  afterEach(() => mounted?.unmount());

  it('failure shows the backend message and stays on the page', async () => {
    const { router } = await mountPage(off, '/dynamic/order');
    api.login.mockRejectedValue({ status: 401, message: '帳號或密碼錯誤，或帳號暫時無法登入' });
    await submit();
    expect(q('login-error')!.textContent).toContain('帳號或密碼錯誤，或帳號暫時無法登入');
    expect(router.currentRoute.value.path).toBe('/login');
    expect(useIdentityStore().userId).toBe('');
  });

  it('伺服器沒有給語系化訊息的失敗（500 的 ProblemDetails 英文標題）顯示「登入失敗」', async () => {
    await mountPage(off);
    api.login.mockRejectedValue({ status: 500, message: 'An error occurred while processing your request.' });
    await submit();
    expect(q('login-error')!.textContent).toBe(zhTW.auth.loginFailed);
  });

  it('真正斷線顯示網路錯誤；其他沒有回應的例外顯示「登入失敗」', async () => {
    await mountPage(off);
    api.login.mockRejectedValue({ status: 0, code: 'network', message: zhTW.common.networkError });
    await submit();
    expect(q('login-error')!.textContent).toBe(zhTW.common.networkError);
    api.login.mockRejectedValue({ status: 0, message: 'x is not a function' });
    await submit();
    expect(q('login-error')!.textContent).toBe(zhTW.auth.loginFailed);
  });

  it('success clears the cache, sets the session and goes to redirect', async () => {
    const { router, queryClient } = await mountPage(off, '/dynamic/order');
    api.login.mockResolvedValue({ userId: 'admin', displayName: 'A', mustChangePassword: false });
    await submit();
    expect(api.login).toHaveBeenCalledWith({ userId: 'Admin', password: 'secret-pw' });
    expect(useIdentityStore().userId).toBe('admin');
    expect(queryClient.getQueryData(['stale'])).toBeUndefined();
    expect(router.currentRoute.value.fullPath).toBe('/dynamic/order');
  });

  it('登入成功後（不重新整理）導到首頁，營運時區已設定', async () => {
    setActivePinia(createPinia());
    resetSessionSettings();
    api.getAuthOptions.mockResolvedValue(off as never);
    api.getMe.mockRejectedValue({ status: 401 });
    api.getEffectivePolicies.mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+07:00' } });
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/login', component: LoginPage, meta: { public: true } }, { path: '/:p(.*)*', component: { template: '<i/>' } }],
    });
    installAuthGuard(router);
    await router.push('/login');
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    mounted = mount(LoginPage, {
      attachTo: document.body,
      global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    await flushPromises();
    expect(api.getEffectivePolicies).not.toHaveBeenCalled();
    api.login.mockResolvedValue({ userId: 'admin', displayName: 'A', mustChangePassword: false });
    try {
      await submit();
      await flushPromises();
      expect(router.currentRoute.value.path).toBe('/');
      expect(api.getEffectivePolicies).toHaveBeenCalledTimes(1);
      expect(businessOffset()).toBe('+07:00');
    } finally {
      setBusinessOffset('+08:00');
    }
  });

  it('an external redirect is ignored', async () => {
    const { router } = await mountPage(off, '//evil.example');
    api.login.mockResolvedValue({ userId: 'admin', displayName: 'A', mustChangePassword: false });
    await submit();
    expect(router.currentRoute.value.fullPath).toBe('/');
  });

  it('mustChangePassword goes to /change-password', async () => {
    const { router } = await mountPage(off, '/dynamic/order');
    api.login.mockResolvedValue({ userId: 'admin', displayName: 'A', mustChangePassword: true });
    await submit();
    expect(useIdentityStore().mustChangePassword).toBe(true);
    expect(router.currentRoute.value.path).toBe('/change-password');
  });

  it('demo mode lists accounts and one click calls demo-login', async () => {
    const { router } = await mountPage(on);
    api.demoLogin.mockResolvedValue({ userId: 'sales01', displayName: '業務一', mustChangePassword: false });
    expect(q('demo-account-sales01')!.textContent).toContain('業務一');
    expect(q('demo-account-floor01')).not.toBeNull();
    q('demo-account-sales01')!.click();
    await flushPromises();
    expect(api.demoLogin).toHaveBeenCalledWith('sales01');
    expect(useIdentityStore().userId).toBe('sales01');
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('demo login syncs isDemoAccount from /me before navigating away', async () => {
    const { router } = await mountPage(on, '/change-password');
    api.demoLogin.mockResolvedValue({ userId: 'sales01', displayName: '業務一', mustChangePassword: false });
    api.getMe.mockResolvedValue({ userId: 'sales01', isDemoAccount: true } as never);
    let flagWhenLeaving: boolean | undefined;
    router.beforeEach((to) => { if (to.path !== '/login') flagWhenLeaving = useIdentityStore().isDemoAccount; });
    q('demo-account-sales01')!.click();
    await flushPromises();
    expect(flagWhenLeaving).toBe(true);
  });

  it('demoGenerating shows the hint together with the admin account the backend still lists', async () => {
    await mountPage({ ...on, demoGenerating: true, demoAccounts: [{ userId: 'admin', displayName: '系統管理員' }] });
    expect(q('demo-generating')!.textContent).toContain(zhTW.auth.demoGenerating);
    expect(q('demo-account-admin')!.textContent).toContain('系統管理員');
  });

  it('demo disabled shows no account list', async () => {
    await mountPage(off);
    expect(q('demo-accounts')).toBeNull();
    expect(q('demo-generating')).toBeNull();
  });

  it('has a language switcher on the login page', async () => {
    await mountPage(off);
    expect(q('locale-open')).not.toBeNull();
  });
  describe('密碼不留在共用快取', () => {
    it('登入請求進行中與成功後，共用快取都不含明文密碼', async () => {
      const { queryClient } = await mountPage(off);
      let resolve!: (u: never) => void;
      api.login.mockReturnValue(new Promise((r) => { resolve = r; }));
      await submit('Admin', 's3cret-ok');
      expect(api.login).toHaveBeenCalledWith({ userId: 'Admin', password: 's3cret-ok' });
      // 成功時 useStartSession 會 qc.clear()，所以要在請求進行中查
      expect(cacheText(queryClient)).not.toContain('s3cret-ok');
      resolve({ userId: 'admin', displayName: 'A', mustChangePassword: false } as never);
      await flushPromises();
      expect(cacheText(queryClient)).not.toContain('s3cret-ok');
    });

    it('登入失敗後共用快取不含明文密碼（密碼欄保留，可修正後重送）', async () => {
      const { queryClient } = await mountPage(off);
      api.login.mockRejectedValue({ status: 401, message: 'x' });
      await submit('Admin', 's3cret-fail');
      expect(api.login).toHaveBeenCalledWith({ userId: 'Admin', password: 's3cret-fail' });
      expect(cacheText(queryClient)).not.toContain('s3cret-fail');
      expect((q('login-password') as HTMLInputElement).value).toBe('s3cret-fail');
    });

    it('失敗後離頁，快取裡留著的 mutation 再執行也讀不到密碼', async () => {
      const { queryClient } = await mountPage(off);
      api.login.mockRejectedValue({ status: 401, message: 'x' });
      await submit('Admin', 's3cret-left');
      await rerunLeftover(queryClient);
      // 綁在當次提交的密碼已取用；再執行就中止，不改送空密碼
      expect(api.login).toHaveBeenCalledTimes(1);
    });

    it('離線時送出不排隊，送的是當次的帳密；之後改欄位、恢復連線也不補送', async () => {
      await mountPage(off);
      api.login.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      onlineManager.setOnline(false);
      try {
        await submit('Admin', 's3cret-first');
        await fill('login-user', 'Other');
        await fill('login-password', 's3cret-second');
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.login.mock.calls).toEqual([[{ userId: 'Admin', password: 's3cret-first' }]]);
      } finally { onlineManager.setOnline(true); }
    });

    it('離線送出後離頁，恢復連線也不再送出', async () => {
      await mountPage(off);
      api.login.mockRejectedValue({ status: 0, code: 'network', message: 'x' });
      onlineManager.setOnline(false);
      try {
        await submit('Admin', 's3cret-gone');
        mounted!.unmount();
        mounted = undefined;
        onlineManager.setOnline(true);
        await flushPromises();
        expect(api.login).toHaveBeenCalledTimes(1);
      } finally { onlineManager.setOnline(true); }
    });
  });
});
