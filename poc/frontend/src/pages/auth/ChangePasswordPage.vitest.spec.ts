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
import ChangePasswordPage from './ChangePasswordPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { changePassword: vi.fn() } }));
const api = vi.mocked(dynamicApi);

let mounted: ReturnType<typeof mount> | undefined;

async function mountPage() {
  setActivePinia(createPinia());
  useIdentityStore().setSession('admin', true);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/change-password', component: ChangePasswordPage }, { path: '/', component: { template: '<i/>' } }],
  });
  await router.push('/change-password');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ChangePasswordPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
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
});
