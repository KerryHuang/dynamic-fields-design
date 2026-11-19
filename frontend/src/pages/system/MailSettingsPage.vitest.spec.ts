import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import type { MailSettings } from 'src/domain/ops/ops-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import MailSettingsPage from './MailSettingsPage.vue';

let calls: Call[];
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
const settings = (o: Partial<MailSettings> = {}): MailSettings => ({
  host: 'smtp.example.com', port: 587, security: 'startTls', userName: 'ops', hasPassword: true, passwordUnreadable: false,
  fromAddress: 'ops@example.com', configured: true, editable: true, ...o,
});

async function mountPage(s: MailSettings, testFails?: HttpFail) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  calls = mockHttp((c) => {
    if (c.url === '/system/mail-settings' && c.method === 'get') return s;
    if (c.url === '/system/mail-settings/test') return testFails ?? { sentTo: ['ops@example.com'] };
    return {};
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(MailSettingsPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const input = (id: string) => (q(id)?.querySelector('input') ?? q(id)) as HTMLInputElement;
async function save() {
  q('mail-save')!.click();
  await flushPromises();
  return bodyOf(calls.find((c) => c.url === '/system/mail-settings' && c.method === 'put')!) as Record<string, unknown>;
}

describe('MailSettingsPage', () => {
  it('密碼留空：PUT 不帶 password 鍵（保留舊密碼），欄位提示「已設定」', async () => {
    const w = await mountPage(settings());
    expect(q('mail-password')!.closest('.q-field')!.textContent).toContain('已設定；留空不變');
    const b = await save();
    expect(b).not.toHaveProperty('password');
    expect(b).toMatchObject({ host: 'smtp.example.com', port: 587, security: 'startTls', userName: 'ops', fromAddress: 'ops@example.com' });
    w.unmount();
  });

  it('填了新密碼就送新值', async () => {
    const w = await mountPage(settings());
    const el = input('mail-password');
    el.value = 'n3w';
    el.dispatchEvent(new Event('input'));
    await flushPromises();
    expect((await save()).password).toBe('n3w');
    w.unmount();
  });

  it('按「清除密碼」送空字串；再按一次「不清除」又回到不送', async () => {
    const w = await mountPage(settings());
    q('mail-clear-password')!.click();
    await flushPromises();
    expect(q('mail-password')!.closest('.q-field')!.textContent).toContain('儲存後會清除密碼');
    expect((await save()).password).toBe('');
    w.unmount();
  });

  it('沒有密碼時不出清除鈕；密碼解不開時提示重填', async () => {
    let w = await mountPage(settings({ hasPassword: false }));
    expect(q('mail-clear-password')).toBeNull();
    w.unmount();
    w = await mountPage(settings({ passwordUnreadable: true }));
    expect(q('mail-password-unreadable')).not.toBeNull();
    w.unmount();
  });

  it('Demo 站（editable false）整頁唯讀：儲存與測試信都停用', async () => {
    const w = await mountPage(settings({ editable: false }));
    expect(q('mail-readonly')).not.toBeNull();
    expect(q('mail-save')!.hasAttribute('disabled')).toBe(true);
    expect(q('mail-test')!.hasAttribute('disabled')).toBe(true);
    w.unmount();
  });

  it('測試信失敗（422）顯示失敗階段', async () => {
    const w = await mountPage(settings(), new HttpFail(422, { code: 'smtpTestFailed', params: { stage: 'auth', serverCode: 535 } }));
    q('mail-test')!.click();
    await flushPromises();
    expect(document.body.textContent).toContain('測試信寄不出去：認證，伺服器回應 535');
    w.unmount();
  });
});
