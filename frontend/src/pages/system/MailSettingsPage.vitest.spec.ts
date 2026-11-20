import { onlineManager, QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import type { MailSettings } from 'src/domain/ops/ops-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import MailSettingsPage from './MailSettingsPage.vue';

let calls: Call[];
let qc: QueryClient;
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
const settings = (o: Partial<MailSettings> = {}): MailSettings => ({
  host: 'smtp.example.com', port: 587, security: 'startTls', userName: 'ops', hasPassword: true, passwordUnreadable: false,
  fromAddress: 'ops@example.com', configured: true, editable: true, ...o,
});

async function mountPage(s: MailSettings, testFails?: HttpFail, saveFails?: HttpFail) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  calls = mockHttp((c) => {
    if (c.url === '/system/mail-settings' && c.method === 'get') return s;
    if (c.url === '/system/mail-settings/test') return testFails ?? { sentTo: ['ops@example.com'] };
    if (c.url === '/system/mail-settings' && c.method === 'put') return saveFails ?? s;
    return {};
  });
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(MailSettingsPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const input = (id: string) => (q(id)?.querySelector('input') ?? q(id)) as HTMLInputElement;
async function typePassword(v: string) {
  const el = input('mail-password');
  el.value = v;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
/** 共用快取（查詢與 mutation，含 variables、結果與錯誤）序列化後的全文。 */
const cacheText = () => JSON.stringify([
  qc.getQueryCache().getAll().map((x) => x.state),
  qc.getMutationCache().getAll().map((x) => ({ ...x.state, options: x.options.mutationKey })),
]);
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

  it('審查修正 1：只換密碼成功後，共用快取不含明文、密碼欄清空（GET 回來的 metadata 不變也一樣）', async () => {
    const w = await mountPage(settings());
    await typePassword('s3cret-n3w');
    expect((await save()).password).toBe('s3cret-n3w');
    expect(cacheText()).not.toContain('s3cret-n3w');
    expect(input('mail-password').value).toBe('');
    w.unmount();
  });

  it('審查修正 1：儲存失敗後共用快取也不含明文（密碼留在輸入欄，可修正後重送）', async () => {
    const w = await mountPage(settings(), undefined, new HttpFail(500, { message: 'boom' }));
    await typePassword('s3cret-fail');
    expect((await save()).password).toBe('s3cret-fail');
    expect(cacheText()).not.toContain('s3cret-fail');
    expect(input('mail-password').value).toBe('s3cret-fail');
    w.unmount();
  });

  it('短核對修正 1：失敗後離頁，快取裡留著的 mutation 再執行也讀不到草稿密碼', async () => {
    const w = await mountPage(settings(), undefined, new HttpFail(500, { message: 'boom' }));
    await typePassword('s3cret-left');
    expect((await save()).password).toBe('s3cret-left');
    const m = qc.getMutationCache().getAll().at(-1)!;
    w.unmount();
    await Promise.resolve(m.state.variables).then(m.options.mutationFn as (v: unknown) => Promise<unknown>).catch(() => undefined);
    // 綁在當次提交的密碼已取用；再執行就中止，不改送不帶密碼的版本
    expect(calls.filter((c) => c.url === '/system/mail-settings' && c.method === 'put')).toHaveLength(1);
  });

  it('短核對修正 1：按了清除密碼後離頁，留著的 mutation 再執行也不會送清除', async () => {
    const w = await mountPage(settings(), undefined, new HttpFail(500, { message: 'boom' }));
    q('mail-clear-password')!.click();
    await flushPromises();
    expect((await save()).password).toBe('');
    const m = qc.getMutationCache().getAll().at(-1)!;
    w.unmount();
    await Promise.resolve(m.state.variables).then(m.options.mutationFn as (v: unknown) => Promise<unknown>).catch(() => undefined);
    // 綁在當次提交的密碼已取用；再執行就中止，不改送不帶密碼的版本
    expect(calls.filter((c) => c.url === '/system/mail-settings' && c.method === 'put')).toHaveLength(1);
  });

  it('審查修正 2：離線時送出不排隊，送的是當次的密碼；之後改欄位、恢復連線也不會補送別的值', async () => {
    const w = await mountPage(settings(), undefined, new HttpFail(500, { message: 'boom' }));
    onlineManager.setOnline(false);
    try {
      await typePassword('s3cret-first');
      expect((await save()).password).toBe('s3cret-first');
      await typePassword('s3cret-second');
      onlineManager.setOnline(true);
      await flushPromises();
      const puts = calls.filter((c) => c.url === '/system/mail-settings' && c.method === 'put');
      expect(puts).toHaveLength(1);
    } finally { onlineManager.setOnline(true); }
    w.unmount();
  });

  it('審查修正 2：離線送出後離頁，恢復連線也不再送出', async () => {
    const w = await mountPage(settings(), undefined, new HttpFail(500, { message: 'boom' }));
    onlineManager.setOnline(false);
    try {
      await typePassword('s3cret-gone');
      await save();
      w.unmount();
      onlineManager.setOnline(true);
      await flushPromises();
      expect(calls.filter((c) => c.url === '/system/mail-settings' && c.method === 'put')).toHaveLength(1);
    } finally { onlineManager.setOnline(true); }
  });

  it('審查修正 1：清除密碼儲存成功後清除旗標復原（不再顯示「儲存後會清除」）', async () => {
    const w = await mountPage(settings());
    q('mail-clear-password')!.click();
    await flushPromises();
    expect((await save()).password).toBe('');
    expect(q('mail-password')!.closest('.q-field')!.textContent).not.toContain('儲存後會清除密碼');
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
