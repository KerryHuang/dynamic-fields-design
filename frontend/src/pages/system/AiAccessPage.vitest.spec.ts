import { onlineManager, QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { McpToken } from 'src/domain/mcp/mcp-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import AiAccessPage from './AiAccessPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getMe: vi.fn(), getUsers: vi.fn(), getAdminMcpTokens: vi.fn(), adminRevokeMcpToken: vi.fn(), adminRevokeAllMcpTokens: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const TOKEN = (id: number, userId: string, over: Partial<McpToken> = {}): McpToken => ({
  id, userId, name: `權杖${id}`, last4: 'abcd', scopes: ['business.read'], createdUtc: '2026-11-21T00:00:00Z', expiresUtc: '2026-12-21T16:00:00Z',
  expiresOn: '2026-12-21', status: 'active', todayCalls: 0, ...over,
});
let mounted: ReturnType<typeof mount> | undefined;
let router: ReturnType<typeof createRouter>;

async function mountPage(isSystemAdmin = true, path = '/system/ai-access') {
  api.getMe.mockResolvedValue({ userId: 'admin', displayName: 'A', isSystemAdmin, groups: [], designer: { read: true, add: true, edit: true, delete: true } } as never);
  api.getUsers.mockResolvedValue([{ userId: 'admin', displayName: '管理員' }, { userId: 'amy', displayName: '艾美' }]);
  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/system/ai-access', component: { template: '<i/>' } }, { path: '/:p(.*)*', component: { template: '<i/>' } }] });
  await router.push(path);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AiAccessPage))) };
  mounted = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, createPinia(), router,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
/** 開 q-select 的選單並點選項。 */
async function pick(id: string, label: string) {
  (q(id)!.closest('.q-field')!.querySelector('.q-field__control') as HTMLElement).click();
  await flushPromises();
  const item = [...document.body.querySelectorAll('.q-menu .q-item')].find((i) => i.textContent?.includes(label)) as HTMLElement;
  item.click();
  await flushPromises();
}
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };

describe('AiAccessPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    onlineManager.setOnline(true);
    api.getAdminMcpTokens.mockResolvedValue([TOKEN(1, 'amy'), TOKEN(2, 'admin', { status: 'revoked', revokeReason: 'admin' })]);
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('non-admins see the forbidden notice and no token request is made', async () => {
    await mountPage(false);
    expect(q('forbidden')).not.toBeNull();
    expect(api.getAdminMcpTokens).not.toHaveBeenCalled();
  });

  it('shows each token id on its card so the id in ops incident titles can be found', async () => {
    await mountPage();
    expect(q('ai-admin-token-1')!.querySelector('[data-testid="ai-token-id"]')!.textContent).toBe('#1');
    expect(q('ai-admin-token-2')!.querySelector('[data-testid="ai-token-id"]')!.textContent).toBe('#2');
  });

  it('lists everyone\'s tokens with the owner name; only active tokens can be revoked', async () => {
    await mountPage();
    expect(api.getAdminMcpTokens).toHaveBeenCalledWith({});
    expect(q('ai-admin-token-1')!.textContent).toContain('艾美');
    expect(q('ai-admin-token-1')!.textContent).toContain('權杖1');
    expect(q('ai-admin-token-2')!.textContent).toContain('已撤銷（管理員撤銷）');
    expect(q('ai-admin-revoke-1')).not.toBeNull();
    expect(q('ai-admin-revoke-2')).toBeNull();
  });

  it('filters by user and status through the API', async () => {
    await mountPage();
    await pick('ai-admin-filter-user', '艾美');
    await pick('ai-admin-filter-status', '有效');
    expect(api.getAdminMcpTokens).toHaveBeenLastCalledWith({ userId: 'amy', status: 'active' });
  });

  it('revokes one token only after the confirmation', async () => {
    api.adminRevokeMcpToken.mockResolvedValue(undefined);
    await mountPage();
    await click('ai-admin-revoke-1');
    expect(api.adminRevokeMcpToken).not.toHaveBeenCalled();
    expect(q('ai-admin-revoke-confirm')!.textContent).toContain('艾美');
    await click('ai-admin-revoke-confirm-yes');
    expect(api.adminRevokeMcpToken).toHaveBeenCalledWith(1);
  });

  it('revokes all of one person\'s tokens after the confirmation and reports the count', async () => {
    api.adminRevokeAllMcpTokens.mockResolvedValue({ revoked: 2 });
    await mountPage();
    await click('ai-admin-revoke-all-1');
    expect(api.adminRevokeAllMcpTokens).not.toHaveBeenCalled();
    expect(q('ai-admin-revoke-all-confirm')!.textContent).toContain('艾美');
    await click('ai-admin-revoke-all-confirm-yes');
    expect(api.adminRevokeAllMcpTokens).toHaveBeenCalledWith('amy');
    expect(q('ai-admin-revoked')!.textContent).toContain('2');
  });

  it('the help tab covers data leaving to AI providers, limiting groups and marking sensitive fields, and links to policies and the audit log', async () => {
    await mountPage(true, '/system/ai-access?tab=help');
    const help = q('ai-admin-help')!;
    expect(help.textContent).toContain('AI 服務商');
    expect(help.textContent).toContain('限定使用對象');
    expect(help.textContent).toContain('不提供給 AI 工具');
    expect(help.querySelector('a[href="/system/policies"]')).not.toBeNull();
    expect(help.querySelector('a[href="/system/audit?channel=mcp"]')).not.toBeNull();
  });

  it('remembers the tab in the URL', async () => {
    await mountPage();
    await click('ai-admin-tab-help');
    expect(router.currentRoute.value.query.tab).toBe('help');
  });
});
