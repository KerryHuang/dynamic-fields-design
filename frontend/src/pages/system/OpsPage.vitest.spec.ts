import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { IncidentSummary, OpsOverview } from 'src/domain/ops/ops-types';
import zhTW from 'src/i18n/zh-TW.json';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import OpsPage from './OpsPage.vue';

let calls: Call[];
let qc: QueryClient;
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const overview = (o: Partial<OpsOverview> = {}): OpsOverview => ({
  overall: 'ok', memoryOnly: false, pendingIncidentWrites: 0, pendingDropped: 0, checks: [],
  openIncidents: { critical: 1, warning: 2, info: 0, byCategory: {} }, needsAttention: [], jobs: [],
  channel: { kind: 'smtp', configured: false, enabled: false, consecutiveFailures: 0 }, version: '1.0.0', commit: 'abc', edition: 'Cloud', database: 'Sqlite', ...o,
});
const incident = (o: Partial<IncidentSummary> = {}): IncidentSummary => ({
  id: 7, fingerprint: 'f', source: 'config', category: 'config', severity: 'warning', status: 'open', titleKey: 'ops.title.rulePaused',
  titleParams: { ruleKey: 'follow', failures: 5 }, code: 'taskRule.autoPaused', occurrenceCount: 1, firstSeenUtc: '2026-11-19T01:00:00Z',
  lastSeenUtc: '2026-11-19T01:00:00Z', firstVersion: '1.0.0', lastVersion: '1.0.0', regression: false, reopenCount: 0, handler: 'designer', manualResolvable: false, ...o,
});

interface Opts { incidentsFail?: HttpFail; overviewFails?: HttpFail; overview?: OpsOverview; pages?: { items: IncidentSummary[]; nextCursor?: string }[]; channel?: Record<string, unknown>; ackFails?: boolean }

async function mountPage(url: string, opts: Opts = {}) {
  window.innerWidth = 1366;
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  const pages = opts.pages ?? [{ items: [incident()] }];
  calls = mockHttp((c) => {
    if (c.url === '/system/ops/overview') return opts.overviewFails ?? opts.overview ?? overview();
    if (c.url === '/system/ops/incidents') return opts.incidentsFail ?? pages[c.params?.cursor ? Number(c.params.cursor) : 0];
    if (c.url === '/system/ops/incidents/7/acknowledge') return opts.ackFails ? new HttpFail(409, { code: 'incidentStateChanged' }) : incident({ status: 'acknowledged' });
    if (c.url === '/system/ops/channels/smtp') return opts.channel ?? { enabled: false, toAddresses: [], mailLocale: { value: null, factory: 'zh-TW', options: ['zh-TW', 'en-US'] }, consecutiveFailures: 0, editable: true, mailConfigured: true };
    return [];
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/system/ops', component: OpsPage }, { path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push(url);
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(OpsPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, router, i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return { w, router };
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('OpsPage', () => {
  it('資料庫連不上（503 databaseUnavailable）整頁改成提示、不顯示分頁', async () => {
    const { w } = await mountPage('/system/ops', { overviewFails: new HttpFail(503, { code: 'databaseUnavailable' }) });
    expect(q('ops-unavailable')?.textContent).toContain('資料庫目前無法連線');
    expect(q('ops-tabs')).toBeNull();
    w.unmount();
  });

  it('審查修正 3：overview 失敗（500）總覽分頁顯示載入失敗，不是空白', async () => {
    const { w } = await mountPage('/system/ops', { overviewFails: new HttpFail(500, { message: 'boom' }) });
    expect(q('ops-overview-failed')).not.toBeNull();
    w.unmount();
  });

  it('審查修正 3：overview 成功、事件查詢失敗（500）顯示載入失敗，不顯示「沒有符合的事件」', async () => {
    const { w } = await mountPage('/system/ops?tab=incidents', { incidentsFail: new HttpFail(500, { message: 'boom' }) });
    expect(q('ops-tabs')).not.toBeNull();
    expect(q('incident-table')!.textContent).toContain('載入失敗');
    expect(q('incident-table')!.textContent).not.toContain('沒有符合的事件');
    expect(q('incident-load-failed')).toBeNull();
    w.unmount();
  });

  it('審查修正 3：已有事件時「載入更多」失敗，清單保留並另出載入失敗橫幅', async () => {
    const opts: Opts = { pages: [{ items: [incident()], nextCursor: '1' }] };
    const { w } = await mountPage('/system/ops?tab=incidents', opts);
    opts.incidentsFail = new HttpFail(500, { message: 'boom' });
    q('incident-more')!.click();
    await flushPromises();
    expect(q('incident-row-7')).not.toBeNull();
    expect(q('incident-load-failed')).not.toBeNull();
    w.unmount();
  });

  it('審查修正 3：overview 成功、事件查詢回 503 databaseUnavailable，立即改成整頁提示', async () => {
    const { w } = await mountPage('/system/ops?tab=incidents', { incidentsFail: new HttpFail(503, { code: 'databaseUnavailable' }) });
    expect(q('ops-unavailable')?.textContent).toContain('資料庫目前無法連線');
    expect(q('ops-tabs')).toBeNull();
    w.unmount();
  });

  it('審查修正 3：總覽成功與子查詢 503 落在同一毫秒時，整頁提示照樣出現', async () => {
    const now = vi.spyOn(Date, 'now').mockReturnValue(Date.UTC(2026, 10, 19, 3, 0, 0));
    try {
      const { w } = await mountPage('/system/ops?tab=incidents', { incidentsFail: new HttpFail(503, { code: 'databaseUnavailable' }) });
      expect(q('ops-unavailable')).not.toBeNull();
      w.unmount();
    } finally {
      now.mockRestore();
    }
  });

  it('審查修正 3：子查詢 503 觸發的整頁提示，在總覽下一次讀取成功後解除', async () => {
    const opts: Opts = { incidentsFail: new HttpFail(503, { code: 'databaseUnavailable' }) };
    const { w } = await mountPage('/system/ops?tab=incidents', opts);
    expect(q('ops-unavailable')).not.toBeNull();
    opts.incidentsFail = undefined; // 資料庫恢復
    await qc.refetchQueries({ queryKey: ['ops-overview'] });
    await flushPromises();
    expect(q('ops-unavailable')).toBeNull();
    expect(q('ops-tabs')).not.toBeNull();
    expect(q('incident-row-7')).not.toBeNull();
    w.unmount();
  });

  it('沒有系統管理權限（403）顯示無權限', async () => {
    const { w } = await mountPage('/system/ops', { overviewFails: new HttpFail(403) });
    expect(q('ops-tabs')).toBeNull();
    expect(q('ops-unavailable')).toBeNull();
    w.unmount();
  });

  it('只存在記憶體時顯示提示與遺失筆數', async () => {
    const { w } = await mountPage('/system/ops', { overview: overview({ memoryOnly: true, pendingDropped: 3 }) });
    expect(q('ops-memory-only')?.textContent).toContain('尚未寫入資料庫');
    expect(q('ops-pending-dropped')?.textContent).toContain('遺失 3 筆');
    w.unmount();
  });

  it('事件分頁：預設篩開啟與已確認、標題依語系組字；不能手動結案的不出結案鈕', async () => {
    const { w } = await mountPage('/system/ops?tab=incidents');
    const list = calls.find((c) => c.url === '/system/ops/incidents')!;
    expect(list.params?.status).toBe('open,acknowledged');
    expect(q('incident-row-7')?.textContent).toContain('任務規則「follow」連續失敗 5 次，已自動暫停');
    expect(q('incident-ack-7')).not.toBeNull();
    expect(q('incident-resolve-7')).toBeNull();
    w.unmount();
  });

  it('事件分頁：有 nextCursor 才有載入更多，按了帶 cursor 取下一頁', async () => {
    const { w } = await mountPage('/system/ops?tab=incidents', { pages: [{ items: [incident()], nextCursor: '1' }, { items: [incident({ id: 8, manualResolvable: true })] }] });
    q('incident-more')!.click();
    await flushPromises();
    expect(calls.filter((c) => c.url === '/system/ops/incidents').map((c) => c.params?.cursor)).toEqual([undefined, '1']);
    expect(q('incident-row-8')).not.toBeNull();
    expect(q('incident-resolve-8')).not.toBeNull();
    expect(q('incident-more')).toBeNull();
    w.unmount();
  });

  it('確認時事件已被別人改過（409）提示並重新整理，送出時帶目前狀態', async () => {
    const { w } = await mountPage('/system/ops?tab=incidents', { ackFails: true });
    q('incident-ack-7')!.click();
    await flushPromises();
    const ack = calls.find((c) => c.url === '/system/ops/incidents/7/acknowledge')!;
    expect(bodyOf(ack)).toEqual({ expectedStatus: 'open' });
    expect(document.body.textContent).toContain('事件狀態已被別人改過');
    expect(calls.filter((c) => c.url === '/system/ops/incidents').length).toBeGreaterThan(1);
    w.unmount();
  });

  it('告警通道分頁：Demo 唯讀、寄信設定未完成時連到寄信設定', async () => {
    const { w } = await mountPage('/system/ops?tab=channel', {
      channel: { enabled: false, toAddresses: [], mailLocale: { value: null, factory: 'zh-TW', options: ['zh-TW'] }, consecutiveFailures: 0, editable: false, mailConfigured: false },
    });
    expect(q('channel-readonly')).not.toBeNull();
    expect(q('channel-to-mail')?.getAttribute('href')).toBe('/system/mail');
    expect(q('channel-save')?.hasAttribute('disabled') || q('channel-save')?.getAttribute('aria-disabled') === 'true').toBe(true);
    w.unmount();
  });
});
