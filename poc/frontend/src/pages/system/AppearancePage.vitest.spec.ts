import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { FACTORY_ME_THEME } from 'src/domain/theme/theme-fixtures';
import type { MeTheme } from 'src/domain/theme/theme-model';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import AppearancePage from './AppearancePage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getMe: vi.fn(), saveSiteTheme: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const SITE: MeTheme = { ...FACTORY_ME_THEME, defaultMode: 'light', defaultStyle: 'enterprise' };
let mounted: ReturnType<typeof mount> | undefined;

async function mountPage(over: { isSystemAdmin?: boolean; theme?: Partial<MeTheme> } = {}) {
  api.getMe.mockResolvedValue({
    userId: 'admin', displayName: 'A', mustChangePassword: false, isSystemAdmin: over.isSystemAdmin ?? true, groups: [],
    designer: { read: true, add: true, edit: true, delete: true }, theme: { ...SITE, ...over.theme },
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AppearancePage))) };
  mounted = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return mounted;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const checked = (id: string) => q(id)?.getAttribute('aria-checked');
const modeButton = (label: string) => [...document.body.querySelectorAll('[data-testid="mode-toggle"] .q-btn')].find((b) => b.textContent?.includes(label)) as HTMLElement;

describe('AppearancePage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('非管理員看到 ForbiddenNotice', async () => {
    await mountPage({ isSystemAdmin: false });
    expect(q('forbidden')).not.toBeNull();
    expect(q('appearance-save')).toBeNull();
  });

  it('顯示目前的全站預設，且沒有跟隨選項', async () => {
    await mountPage();
    expect(checked('style-enterprise')).toBe('true');
    expect(checked('style-airy')).toBe('false');
    expect(q('style-follow')).toBeNull();
    expect(document.body.textContent).toContain(zhTW.theme.adminHint);
  });

  it('改了要按儲存才送出，送出的值正確', async () => {
    api.saveSiteTheme.mockResolvedValue({ mode: 'dark', style: 'industrial' });
    await mountPage();
    modeButton('深色').click();
    q('style-industrial')!.click();
    await flushPromises();
    expect(api.saveSiteTheme).not.toHaveBeenCalled();
    q('appearance-save')!.click();
    await flushPromises();
    expect(api.saveSiteTheme).toHaveBeenCalledWith({ mode: 'dark', style: 'industrial' });
  });

  it('儲存失敗顯示錯誤，草稿回到原值', async () => {
    api.saveSiteTheme.mockRejectedValue(new Error('500'));
    await mountPage();
    q('style-industrial')!.click();
    await flushPromises();
    expect(checked('style-industrial')).toBe('true');
    q('appearance-save')!.click();
    await flushPromises();
    expect(q('appearance-error')?.textContent).toContain(zhTW.theme.saveFailed);
    expect(checked('style-industrial')).toBe('false');
    expect(checked('style-enterprise')).toBe('true');
  });

  it('Demo 站唯讀、說明原因，沒有儲存鈕', async () => {
    await mountPage({ theme: { canSave: false } });
    expect(q('appearance-demo-readonly')?.textContent).toContain(zhTW.theme.adminDemoReadOnly);
    expect(q('appearance-save')).toBeNull();
    q('style-industrial')!.click();
    await flushPromises();
    expect(checked('style-industrial')).toBe('false');
  });
});
