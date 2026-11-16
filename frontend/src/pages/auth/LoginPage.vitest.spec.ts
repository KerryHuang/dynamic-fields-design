import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import LoginPage from './LoginPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getAuthOptions: vi.fn(), login: vi.fn(), demoLogin: vi.fn(), getMe: vi.fn() },
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
});
