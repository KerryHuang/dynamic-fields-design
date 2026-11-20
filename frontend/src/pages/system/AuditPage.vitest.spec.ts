import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { AuditDetail, AuditSummary } from 'src/domain/ops/ops-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { findInputByTestId } from 'src/test-utils/find-by-testid';
import { HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import AuditPage from './AuditPage.vue';

let calls: Call[];
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const entry = (o: Partial<AuditSummary> = {}): AuditSummary => ({
  id: 41, occurredUtc: '2026-11-19T02:00:00Z', actorUserId: 'admin', actorName: '管理員', actorKind: 'user', channel: 'ui', action: 'ops.channel.update',
  riskLevel: 'highRisk', targetKind: 'channel', targetId: 'smtp', result: 'success', requestId: 'req-1', ...o,
});
const detail = (o: Partial<AuditDetail> = {}): AuditDetail => ({
  ...entry(), summary: { before: { enabled: false }, after: { enabled: true } }, ...o,
} as AuditDetail);

async function mountPage(url: string, opts: { forbidden?: boolean; detail?: AuditDetail } = {}) {
  window.innerWidth = 1366;
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  calls = mockHttp((c) => {
    if (c.url === '/system/ops/audit') return opts.forbidden ? new HttpFail(403) : { items: [entry()] };
    if (c.url === '/system/ops/audit/actions') return [
      { code: 'ops.channel.update', group: 'ops', risk: 'highRisk', labelKey: 'audit.actions.ops.channel.update' },
      { code: 'login.failed', group: 'auth', risk: 'read', labelKey: 'audit.actions.login.failed' },
    ];
    if (c.url === '/system/ops/audit/41') return opts.detail ?? detail();
    if (c.url === '/users') return [{ userId: 'admin', displayName: '管理員' }];
    return [];
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/system/audit', component: AuditPage }, { path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push(url);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AuditPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], pinia, router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return { w, router };
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const lists = () => calls.filter((c) => c.url === '/system/ops/audit');

describe('AuditPage', () => {
  it('清單顯示動作標籤、目標與結果；沒填篩選時不送任何條件', async () => {
    const { w } = await mountPage('/system/audit');
    expect(lists()[0]!.params).toEqual({ cursor: undefined });
    const row = q('audit-row-41')!.textContent!;
    expect(row).toContain('修改告警通道');
    expect(row).toContain('告警通道：smtp');
    expect(row).toContain('成功');
    w.unmount();
  });

  it('選動作分組送 actionGroup、選結果送 result；網址的目標篩選一起送', async () => {
    const { w } = await mountPage('/system/audit?targetKind=incident&targetId=7');
    expect(q('audit-target-filter')).not.toBeNull();
    findInputByTestId(w, 'audit-action').vm.$emit('update:modelValue', 'group:auth');
    findInputByTestId(w, 'audit-result').vm.$emit('update:modelValue', 'denied');
    await flushPromises();
    expect(lists().at(-1)!.params).toMatchObject({ actionGroup: 'auth', result: 'denied', targetKind: 'incident', targetId: '7' });
    findInputByTestId(w, 'audit-action').vm.$emit('update:modelValue', 'login.failed');
    await flushPromises();
    expect(lists().at(-1)!.params).toMatchObject({ action: 'login.failed' });
    expect(lists().at(-1)!.params).not.toHaveProperty('actionGroup');
    w.unmount();
  });

  it('起訖日換成 UTC 區間：營運時區起日當天 0 點、迄日當天最後一毫秒', async () => {
    const { w } = await mountPage('/system/audit');
    const dates = w.findAllComponents({ name: 'DateSelect' });
    dates[0]!.vm.$emit('update:modelValue', '2026-11-01');
    dates[1]!.vm.$emit('update:modelValue', '2026-11-02');
    await flushPromises();
    const p = lists().at(-1)!.params as Record<string, string>;
    expect(p.fromUtc).toBe('2026-10-31T16:00:00.000Z');
    expect(p.toUtc).toBe('2026-11-02T15:59:59.999Z');
    w.unmount();
  });

  it('列表時間標時區、日期篩選標示依營運時區', async () => {
    const { w } = await mountPage('/system/audit');
    expect(q('audit-row-41')!.textContent).toContain('2026-11-19 10:00 +08:00');
    expect(q('audit-from')!.closest('.q-field')!.textContent).toContain('UTC+08:00');
    w.unmount();
  });

  it('?id= 直接開詳細：差異逐欄列出，沒有來源 IP 就不列那一欄', async () => {
    const { w } = await mountPage('/system/audit?id=41');
    expect(q('audit-diff')?.textContent).toContain('enabled');
    expect(q('audit-field-requestId')).not.toBeNull();
    expect(q('audit-field-clientIp')).toBeNull();
    w.unmount();
  });

  it('MCP 呼叫的詳細列出權杖、參數雜湊、確認 id 與確認結果；沒有的欄位不列', async () => {
    const { w } = await mountPage('/system/audit?id=41', {
      detail: detail({ channel: 'mcp', tokenId: 12, argumentsHash: 'sha256-abc', confirmationId: 88, confirmationOutcome: 'mismatch' }),
    });
    expect(q('audit-field-tokenId')?.textContent).toContain('12');
    expect(q('audit-field-argumentsHash')?.textContent).toContain('sha256-abc');
    expect(q('audit-field-confirmation')?.textContent).toContain('參數不符（88）');
    w.unmount();
    const again = await mountPage('/system/audit?id=41');
    expect(q('audit-field-tokenId')).toBeNull();
    expect(q('audit-field-argumentsHash')).toBeNull();
    expect(q('audit-field-confirmation')).toBeNull();
    again.w.unmount();
  });

  it('沒有權限顯示無權限、不出清單', async () => {
    const { w } = await mountPage('/system/audit', { forbidden: true });
    expect(q('audit-table')).toBeNull();
    w.unmount();
  });
});
