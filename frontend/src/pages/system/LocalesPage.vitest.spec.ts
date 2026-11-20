import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import LocalesPage from './LocalesPage.vue';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const base = { enabled: true, isDefault: false, required: false, overrides: { ui: 0, server: 0 }, current: null, hasPrevious: false, fallbackCode: null };
const locale = (o: Record<string, unknown>) => ({ ...base, ...o });
const LOCALES = [
  locale({ code: 'zh-TW', nativeName: '繁體中文', origin: 'builtin', isDefault: true, required: true }),
  locale({ code: 'th', nativeName: 'ไทย', origin: 'builtin', overrides: { ui: 30, server: 4 }, hasPrevious: true,
    current: { packVersion: '1.2.0', fileName: 'th-fix.langpack.json', importedUtc: '2026-11-21T02:00:00Z', importedBy: 'admin' } }),
  locale({ code: 'vi', nativeName: 'Tiếng Việt', origin: 'builtin', enabled: false,
    current: { packVersion: null, fileName: null, importedUtc: '2026-11-21T03:00:00Z', importedBy: 'admin' } }),
  locale({ code: 'ja', nativeName: '日本語', origin: 'installed', enabled: false, fallbackCode: 'en-US', overrides: { ui: 1790, server: 160 },
    current: { packVersion: '0.9.0', fileName: 'ja.langpack.json', importedUtc: '2026-11-21T02:00:00Z', importedBy: 'admin' } }),
];

let calls: Call[];
let qc: QueryClient;
async function mountPage(opts: { importEnabled?: boolean; forbidden?: boolean; reply?: (c: Call) => unknown } = {}) {
  window.innerWidth = 1366;
  const pinia = createPinia();
  setActivePinia(pinia);
  calls = mockHttp((c) => {
    const custom = opts.reply?.(c);
    if (custom !== undefined) return custom;
    if (c.url === '/i18n/locales' && c.method === 'get') {
      return opts.forbidden ? new HttpFail(403) : { importEnabled: opts.importEnabled ?? true, packMaxBytes: 5242880, maxInstalled: 10, locales: LOCALES };
    }
    return {};
  });
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(LocalesPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, router, i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  await new Promise((r) => setTimeout(r, 50));
  await flushPromises();
  return w;
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const isDisabled = (el: HTMLElement | null) => el?.getAttribute('aria-disabled') === 'true' || el?.classList.contains('disabled');
async function openMenu(code: string) {
  q(`locale-actions-${code}`)!.click();
  await new Promise((r) => setTimeout(r, 50));
  await flushPromises();
}
const tick = async () => { await new Promise((r) => setTimeout(r, 50)); await flushPromises(); };

describe('LocalesPage 清單', () => {
  it('列出四個內建與匯入的語系：自稱、來源、啟用與預設標籤', async () => {
    const w = await mountPage();
    expect(document.querySelectorAll('[data-testid^="locale-row-"]')).toHaveLength(4);
    expect(q('locale-row-th')!.textContent).toContain('ไทย');
    expect(q('locale-row-th')!.textContent).toContain('內建');
    expect(q('locale-row-ja')!.textContent).toContain('匯入');
    expect(q('locale-row-zh-TW')!.textContent).toContain('預設');
    expect(q('locale-row-zh-TW')!.textContent).toContain('啟用中');
    expect(q('locale-row-vi')!.textContent).not.toContain('啟用中');
    w.unmount();
  });

  it('有覆寫才顯示句數；有目前版才顯示版本與時間（營運時區），空版本顯示已清空；current 為 null 不顯示', async () => {
    const w = await mountPage();
    expect(q('locale-row-th')!.textContent).toContain('覆寫 介面 30 句、系統訊息 4 句');
    expect(q('locale-row-th')!.textContent).toContain('1.2.0');
    expect(q('locale-row-th')!.textContent).toMatch(/2026-11-21 \d\d:\d\d [+-]\d\d:\d\d/);
    expect(q('locale-version-vi')!.textContent).toContain('清空');
    expect(q('locale-version-zh-TW')).toBeNull();
    expect(q('locale-row-zh-TW')!.textContent).not.toContain('覆寫 介面');
    w.unmount();
  });

  it('「⋯」選單：內建沒有移除語系；沒有上一版沒有回到上一版；沒有覆寫沒有移除覆寫', async () => {
    const w = await mountPage();
    await openMenu('th');
    expect(q('locale-rollback-th')).not.toBeNull();
    expect(q('locale-clear-th')).not.toBeNull();
    expect(q('locale-remove-th')).toBeNull();
    document.body.click();
    await tick();
    await openMenu('ja');
    expect(q('locale-remove-ja')).not.toBeNull();
    expect(q('locale-rollback-ja')).toBeNull();
    w.unmount();
  });

  it('importEnabled 為 false：顯示提示，三個動作停用', async () => {
    const w = await mountPage({ importEnabled: false });
    expect(q('locales-import-disabled')).not.toBeNull();
    await openMenu('th');
    expect(isDisabled(q('locale-rollback-th'))).toBe(true);
    expect(isDisabled(q('locale-clear-th'))).toBe(true);
    w.unmount();
  });

  it('403 顯示沒有權限，不列清單', async () => {
    const w = await mountPage({ forbidden: true });
    expect(document.querySelector('[data-testid^="locale-row-"]')).toBeNull();
    w.unmount();
  });
});

describe('LocalesPage 動作', () => {
  const invalidated = (spy: ReturnType<typeof vi.spyOn>) => spy.mock.calls.map((c: unknown[]) => JSON.stringify((c[0] as { queryKey: unknown }).queryKey));
  const expectKeys = (spy: ReturnType<typeof vi.spyOn>) => {
    const keys = invalidated(spy);
    for (const k of ['["locale-admin"]', '["effective-policies"]', '["auth-options"]']) expect(keys).toContain(k);
  };

  it('回到上一版：確認框 → POST rollback → 失效三個 query key', async () => {
    const w = await mountPage();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await openMenu('th');
    q('locale-rollback-th')!.click();
    await tick();
    expect(q('locale-confirm')!.textContent).toContain('可以再按一次切回來');
    q('locale-confirm-ok')!.click();
    await tick();
    expect(calls.some((c) => c.method === 'post' && c.url === '/i18n/locales/th/rollback')).toBe(true);
    expectKeys(spy);
    w.unmount();
  });

  it('移除覆寫：確認框寫明可用回到上一版復原 → POST clear → 失效三個 key', async () => {
    const w = await mountPage();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await openMenu('th');
    q('locale-clear-th')!.click();
    await tick();
    expect(q('locale-confirm')!.textContent).toContain('回到上一版');
    q('locale-confirm-ok')!.click();
    await tick();
    expect(calls.some((c) => c.method === 'post' && c.url === '/i18n/locales/th/clear')).toBe(true);
    expectKeys(spy);
    w.unmount();
  });

  it('移除語系：確認框含語系自稱 → DELETE → 失效三個 key', async () => {
    const w = await mountPage();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await openMenu('ja');
    q('locale-remove-ja')!.click();
    await tick();
    expect(q('locale-confirm')!.textContent).toContain('日本語');
    q('locale-confirm-ok')!.click();
    await tick();
    expect(calls.some((c) => c.method === 'delete' && c.url === '/i18n/locales/ja')).toBe(true);
    expectKeys(spy);
    w.unmount();
  });

  it('後端擋下（409）時顯示後端訊息、不失效', async () => {
    const w = await mountPage({
      reply: (c) => (c.method === 'delete' ? new HttpFail(409, { code: 'localeInUse', message: '請先在政策設定停用這個語系' }) : undefined),
    });
    const spy = vi.spyOn(qc, 'invalidateQueries');
    await openMenu('ja');
    q('locale-remove-ja')!.click();
    await tick();
    q('locale-confirm-ok')!.click();
    await tick();
    expect(document.body.textContent).toContain('請先在政策設定停用這個語系');
    expect(invalidated(spy)).not.toContain('["locale-admin"]');
    w.unmount();
  });
});
