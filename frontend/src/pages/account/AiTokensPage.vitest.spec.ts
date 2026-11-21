import { onlineManager, QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { McpInfo, McpToken } from 'src/domain/mcp/mcp-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import AiTokensPage from './AiTokensPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getMyMcp: vi.fn(), getMyMcpTokens: vi.fn(), createMcpToken: vi.fn(), revokeMyMcpToken: vi.fn(), getMcpTokenCalls: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const INFO: McpInfo = {
  enabled: true, canUse: true, reason: null, scopes: ['business.read', 'business.write'], defaultScopes: ['business.read'], defaultExpiresOn: '2026-12-21', maxExpiresOn: '2027-02-19',
  serverUrl: 'https://fw.example/mcp',
  examples: { claudeCode: "claude mcp add --transport http formwright https://fw.example/mcp --header 'Authorization: Bearer ${FORMWRIGHT_TOKEN}'", codex: '[mcp_servers.formwright]\nurl = "https://fw.example/mcp"' },
};
const TOKEN = (over: Partial<McpToken> = {}): McpToken => ({
  id: 1, userId: 'admin', name: '筆電', last4: 'abcd', scopes: ['business.read'], createdUtc: '2026-11-21T00:00:00Z', expiresUtc: '2026-12-21T16:00:00Z',
  expiresOn: '2026-12-21', status: 'active', todayCalls: 3, ...over,
});

let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;
const mountPage = async () => {
  setActivePinia(createPinia());
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AiTokensPage))) };
  mounted = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
};
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
/** q-dialog 關閉有轉場，節點要等動畫結束才移除。 */
const gone = (id: string) => vi.waitFor(() => expect(q(id)).toBeNull());
async function fill(id: string, value: string) {
  const el = q(id) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
const CREATED = { token: 'fwp_SECRETSECRET', item: TOKEN({ id: 2, name: '新的' }) };

describe('AiTokensPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    onlineManager.setOnline(true);
    api.getMyMcp.mockResolvedValue(INFO);
    api.getMyMcpTokens.mockResolvedValue([TOKEN()]);
    api.getMcpTokenCalls.mockResolvedValue([]);
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('shows the token id on the card so it matches the id in ops incident titles', async () => {
    api.getMyMcpTokens.mockResolvedValue([TOKEN({ id: 12 })]);
    await mountPage();
    expect(q('ai-token-12')!.querySelector('[data-testid="ai-token-id"]')!.textContent).toBe('#12');
  });

  it('lists tokens with status, expiry and today calls', async () => {
    await mountPage();
    const card = q('ai-token-1')!;
    expect(card.textContent).toContain('筆電');
    expect(card.textContent).toContain('2026-12-21');
    expect(card.textContent).toContain('有效');
    expect(card.textContent).toContain('3');
    expect(q('ai-reason')).toBeNull();
  });

  it.each([
    ['disabled', '系統管理員已關閉 AI 連線'],
    ['notAllowed', '你不在可使用 AI 連線的對象內'],
    ['policyInvalid', '政策設定有誤或無法讀取'],
    ['demoSite', 'Demo 站不開放 AI 連線'],
    ['accountBlocked', '這個帳號不能使用 AI 連線'],
  ] as const)('shows the %s reason, disables create, and still lists existing tokens', async (reason, text) => {
    api.getMyMcp.mockResolvedValue({ ...INFO, canUse: false, reason });
    await mountPage();
    expect(q('ai-reason')!.textContent).toContain(text);
    expect(q('ai-create')!.hasAttribute('disabled')).toBe(true);
    expect(q('ai-token-1')).not.toBeNull();
  });

  it('keeps create disabled until the info has been read', async () => {
    api.getMyMcp.mockReturnValue(new Promise(() => {}));
    await mountPage();
    expect(q('ai-create')!.hasAttribute('disabled')).toBe(true);
  });

  it('shows revoked tokens with the reason, and offers no revoke', async () => {
    api.getMyMcpTokens.mockResolvedValue([TOKEN({ status: 'revoked', revokeReason: 'passwordReset' })]);
    await mountPage();
    expect(q('ai-token-1')!.textContent).toContain('已撤銷（管理員重設密碼）');
    expect(q('ai-revoke-1')).toBeNull();
  });

  it('expands a token to show its recent calls', async () => {
    api.getMcpTokenCalls.mockResolvedValue([{ id: 9, action: 'mcp.tool.call', tool: 'list_records', occurredUtc: '2026-11-21T02:00:00Z', result: 'success' }]);
    await mountPage();
    expect(api.getMcpTokenCalls).not.toHaveBeenCalled();
    await click('ai-expand-1');
    expect(api.getMcpTokenCalls).toHaveBeenCalledWith(1);
    expect(q('ai-calls-1')!.textContent).toContain('list_records');
  });

  it('revokes only after the confirmation', async () => {
    api.revokeMyMcpToken.mockResolvedValue(undefined);
    await mountPage();
    await click('ai-revoke-1');
    expect(api.revokeMyMcpToken).not.toHaveBeenCalled();
    expect(q('ai-revoke-confirm')!.textContent).toContain('筆電');
    await click('ai-revoke-confirm-yes');
    expect(api.revokeMyMcpToken).toHaveBeenCalledWith(1);
  });

  it('creates a token with the API defaults and shows the plaintext once with connection examples', async () => {
    api.createMcpToken.mockResolvedValue(CREATED);
    await mountPage();
    await click('ai-create');
    expect((q('ai-expires') as HTMLInputElement).value).toBe(INFO.defaultExpiresOn);
    expect(q('ai-scopes')!.textContent).toContain('讀取業務資料');
    expect(document.body.textContent).toContain(INFO.maxExpiresOn);
    await fill('ai-name', '新的');
    await fill('ai-password', 'pw-123456');
    await click('ai-submit');
    expect(api.createMcpToken).toHaveBeenCalledWith({ name: '新的', scopes: ['business.read'], expiresOn: INFO.defaultExpiresOn, password: 'pw-123456' });
    expect(q('ai-secret')!.textContent).toContain('fwp_SECRETSECRET');
    expect(q('ai-example-claude')!.textContent).toContain('claude mcp add --transport http formwright https://fw.example/mcp');
    expect(q('ai-example-codex')!.textContent).toContain('[mcp_servers.formwright]');
    expect(q('ai-example-env')!.textContent).toContain('FORMWRIGHT_TOKEN');
    // 明文與密碼都不留在查詢與 mutation 快取
    const cache = JSON.stringify([queryClient.getMutationCache().getAll().map((m) => m.state.variables)]);
    expect(cache).not.toContain('pw-123456');
  });

  it('asks before closing the one-time screen, and closing wipes the plaintext', async () => {
    api.createMcpToken.mockResolvedValue(CREATED);
    await mountPage();
    await click('ai-create');
    await fill('ai-name', '新的');
    await fill('ai-password', 'pw-123456');
    await click('ai-submit');
    await click('ai-secret-close');
    expect(q('ai-close-confirm')).not.toBeNull();
    expect(q('ai-secret')!.textContent).toContain('fwp_SECRETSECRET');
    await click('ai-close-keep');
    await gone('ai-close-confirm');
    expect(q('ai-secret')).not.toBeNull();
    await click('ai-secret-close');
    await click('ai-close-anyway');
    await gone('ai-secret');
    expect(document.body.textContent).not.toContain('fwp_SECRETSECRET');
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain('fwp_');
  });

  const esc = async () => {
    // Quasar 的 Esc 要 keydown 再 keyup（都掛在 window）才算一次
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27 }));
    window.dispatchEvent(new KeyboardEvent('keyup', { key: 'Escape', keyCode: 27 }));
    await flushPromises();
  };

  it('Esc closes the form screen directly (nothing to lose yet)', async () => {
    await mountPage();
    await click('ai-create');
    expect(q('ai-dialog')).not.toBeNull();
    await esc();
    await gone('ai-dialog');
  });

  it('Esc on the one-time screen asks first and keeps the plaintext', async () => {
    api.createMcpToken.mockResolvedValue(CREATED);
    await mountPage();
    await click('ai-create');
    await fill('ai-name', 'x');
    await fill('ai-password', 'p');
    await click('ai-submit');
    await esc();
    expect(q('ai-close-confirm')).not.toBeNull();
    expect(q('ai-secret')!.textContent).toContain('fwp_SECRETSECRET');
  });

  it('sends null for an empty or half-typed expiry so the server answers with its own message', async () => {
    api.createMcpToken.mockResolvedValue(CREATED);
    await mountPage();
    await click('ai-create');
    await fill('ai-name', 'x');
    await fill('ai-password', 'p');
    await fill('ai-expires', '2026-1');
    await click('ai-submit');
    expect(api.createMcpToken.mock.calls[0]![0]).toMatchObject({ expiresOn: null });
  });

  it('checks only the API default scopes when the dialog opens; write must be ticked explicitly', async () => {
    api.createMcpToken.mockResolvedValue(CREATED);
    await mountPage();
    await click('ai-create');
    expect(q('ai-scope-business.read')!.getAttribute('aria-checked')).toBe('true');
    expect(q('ai-scope-business.write')!.getAttribute('aria-checked')).toBe('false');
    expect(q('ai-scope-business.write')!.textContent).toContain('新增與修改業務資料');
    await click('ai-scope-business.write');
    await fill('ai-name', '可寫');
    await fill('ai-password', 'pw-123456');
    await click('ai-submit');
    expect(api.createMcpToken.mock.calls[0]![0]).toMatchObject({ scopes: ['business.read', 'business.write'] });
  });

  it('labels the scope checkbox with the plain-language text, the code only as a caption', async () => {
    await mountPage();
    await click('ai-create');
    expect(q('ai-scope-business.read')!.getAttribute('aria-label') ?? q('ai-scope-business.read')!.textContent).toContain('讀取業務資料');
  });

  it('shows an error when me/mcp cannot be read', async () => {
    api.getMyMcp.mockRejectedValue({ status: 500 });
    await mountPage();
    expect(q('ai-info-error')).not.toBeNull();
  });

  it('falls back to a generic text for an unknown reason and for a revoked token without a reason', async () => {
    api.getMyMcp.mockResolvedValue({ ...INFO, canUse: false, reason: 'brandNew' as never });
    api.getMyMcpTokens.mockResolvedValue([TOKEN({ status: 'revoked' })]);
    await mountPage();
    expect(q('ai-reason')!.textContent).not.toContain('aiTokens.');
    expect(q('ai-token-1')!.textContent).toContain('已撤銷');
    expect(q('ai-token-1')!.textContent).not.toContain('（）');
  });

  it('copies the plaintext', async () => {
    api.createMcpToken.mockResolvedValue(CREATED);
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await mountPage();
    await click('ai-create');
    await fill('ai-name', 'x');
    await fill('ai-password', 'p');
    await click('ai-submit');
    await click('ai-copy');
    expect(writeText).toHaveBeenCalledWith('fwp_SECRETSECRET');
  });

  it('shows the server message when creation is rejected and keeps the dialog', async () => {
    api.createMcpToken.mockRejectedValue({ status: 400, errors: { password: ['密碼不正確'] } });
    await mountPage();
    await click('ai-create');
    await fill('ai-name', 'x');
    await fill('ai-password', 'bad');
    await click('ai-submit');
    expect(q('ai-create-error')!.textContent).toContain('密碼不正確');
    expect(q('ai-secret')).toBeNull();
  });
  it('hints under the write scope that it needs read, and shows the server 400 when only write is sent', async () => {
    api.createMcpToken.mockRejectedValue({ status: 400, errors: { scopes: ['勾選寫入範圍時，也要勾選同一類的讀取範圍'] } });
    await mountPage();
    await click('ai-create');
    expect(q('ai-scope-hint-business.write')!.textContent).toContain('同時勾選');
    expect(q('ai-scope-hint-business.read')).toBeNull();
    await click('ai-scope-business.read');
    await click('ai-scope-business.write');
    await fill('ai-name', '只寫');
    await fill('ai-password', 'pw-123456');
    await click('ai-submit');
    expect(api.createMcpToken.mock.calls[0]![0]).toMatchObject({ scopes: ['business.write'] });
    expect(q('ai-create-error')!.textContent).toContain('也要勾選同一類的讀取範圍');
    expect(q('ai-secret')).toBeNull();
  });
});
