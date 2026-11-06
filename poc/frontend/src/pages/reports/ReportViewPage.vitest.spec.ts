import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ModuleCatalog, ReportDetail, ReportResult } from 'src/domain/dynamic/report-types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ReportViewPage from './ReportViewPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getReport: vi.fn(), runReport: vi.fn(), drillReport: vi.fn(), copyReport: vi.fn(), deleteReport: vi.fn(),
    exportReport: vi.fn(), getReportCatalog: vi.fn(), setReportShares: vi.fn(), getGroups: vi.fn(), getMe: vi.fn(),
  },
}));
vi.mock('./../../presentation/reports/ReportChart.vue', () => ({ default: { template: '<i />' } }));
const api = vi.mocked(dynamicApi);

const DEF: ReportDetail['definition'] = {
  version: 1, mainModuleKey: 'orders', columns: [], measures: [{ id: 'm1', kind: 'sum', field: { source: 'main', fieldId: 2 } }],
  groups: [{ field: { source: 'main', fieldId: 1 } }], viz: 'table',
  filters: [
    { field: { source: 'main', fieldId: 3 }, op: 'eq', value: '已結案' },
    { field: { source: 'main', fieldId: 4 }, op: 'eq', hidden: true },
  ],
};
const DETAIL = (isOwner: boolean): ReportDetail => ({
  id: 7, name: '月報', ownerName: '小王', isOwner, sharedGroupIds: [], mainModuleKey: 'orders', definition: DEF, canExport: true,
});
const RESULT: ReportResult = {
  state: 'ok', rowMeaning: '一張訂單', basisDate: '2026-11-05', dedupedNote: false,
  columns: [
    { key: 'main:1:', label: '客戶', role: 'group', valueType: 'text' },
    { key: 'm1', label: '金額', role: 'measure', valueType: 'number' },
  ],
  groups: [{ groupKeys: ['a'], groupLabels: ['甲'], values: { m1: 5 } }], total: { m1: 5 },
};
const OPT = (id: string, label: string, active = true) => ({ id, label, active });
const CATALOG: ModuleCatalog = {
  fields: [
    { id: 5, label: '狀態', type: 'enum', canGroup: true, canSum: false, canMinMax: false, isDate: false, options: [OPT('opt_1', '進行中'), OPT('opt_2', '舊狀態', false)] },
  ],
  via: [{ fieldId: 6, label: '客戶', targetKey: 'e', fields: [{ id: 0, label: '標題', type: 'text', canGroup: true, canSum: false, canMinMax: false, isDate: false, options: null }] }],
  children: [],
};
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;
let router: ReturnType<typeof createRouter>;

async function mountPage(detail: ReportDetail, isSystemAdmin = false) {
  api.getMe.mockResolvedValue({ userId: 'u', displayName: 'u', isSystemAdmin, mustChangePassword: false, groups: [], designer: { read: false, add: false, edit: false, delete: false } });
  api.getReport.mockResolvedValue(detail);
  api.getReportCatalog.mockResolvedValue(CATALOG);
  api.runReport.mockResolvedValue(RESULT);
  router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/reports', component: { template: '<i/>' } }, { path: '/reports/:id(\\d+)', component: ReportViewPage }],
  });
  await router.push('/reports/7');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount({ template: '<q-layout><q-page-container><router-view /></q-page-container></q-layout>' }, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, router, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
}

describe('ReportViewPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('owner sees edit, share and delete but not save as copy', async () => {
    await mountPage(DETAIL(true));
    expect(q('report-edit')).not.toBeNull();
    expect(q('report-share')).not.toBeNull();
    expect(q('report-delete')).not.toBeNull();
    expect(q('report-copy')).toBeNull();
    expect(q('group-row-0')!.textContent).toContain('甲');
  });

  it('shared report hides edit and shows save as copy', async () => {
    await mountPage(DETAIL(false));
    expect(q('report-edit')).toBeNull();
    expect(q('report-share')).toBeNull();
    expect(q('report-delete')).toBeNull();
    expect(q('report-copy')).not.toBeNull();
  });

  it('system admin who is not the owner can delete but not edit or share', async () => {
    await mountPage(DETAIL(false), true);
    expect(q('report-delete')).not.toBeNull();
    expect(q('report-edit')).toBeNull();
    expect(q('report-share')).toBeNull();
  });

  it('copy 403 explains the same as 400', async () => {
    await mountPage(DETAIL(false));
    api.copyReport.mockRejectedValue({ status: 403 });
    q('report-copy')!.click();
    await flushPromises();
    expect(q('copy-error')!.textContent).toBe('這張報表用到你看不到的欄位，無法另存');
  });

  it('save as copy goes to the new report', async () => {
    await mountPage(DETAIL(false));
    api.copyReport.mockResolvedValue({ ...DETAIL(true), id: 8 });
    q('report-copy')!.click();
    await flushPromises();
    expect(api.copyReport).toHaveBeenCalledWith(7);
    expect(router.currentRoute.value.path).toBe('/reports/8');
  });

  it('copy 400 explains the report uses fields the viewer cannot see, not a generic error', async () => {
    await mountPage(DETAIL(false));
    api.copyReport.mockRejectedValue({ status: 400, errors: { definition: ['x'] } });
    q('report-copy')!.click();
    await flushPromises();
    expect(q('copy-error')!.textContent).toBe('這張報表用到你看不到的欄位，無法另存');
  });

  it('export disabled when forbidden (403 shows the message)', async () => {
    await mountPage(DETAIL(true));
    api.exportReport.mockRejectedValue({ status: 403 });
    q('report-export')!.click();
    await flushPromises();
    expect(q('export-error')!.textContent).toBe('沒有匯出權限');
  });

  it('export button is disabled when the backend says canExport is false', async () => {
    await mountPage({ ...DETAIL(true), canExport: false });
    expect(q('report-export')!.hasAttribute('disabled') || q('report-export')!.classList.contains('disabled')).toBe(true);
    q('report-export')!.click();
    await flushPromises();
    expect(api.exportReport).not.toHaveBeenCalled();
  });

  it('masked conditions show as hidden condition without a value', async () => {
    await mountPage(DETAIL(false));
    q('report-conditions')!.querySelector('.q-item')!.dispatchEvent(new Event('click'));
    await flushPromises();
    expect(document.body.querySelectorAll('[data-testid="condition-hidden"]')).toHaveLength(1);
    expect(q('condition-hidden')!.textContent!.trim()).toBe('隱藏條件');
    expect(q('condition')!.textContent).toContain('已結案');
  });

  it('a failed run shows a retry, not a number', async () => {
    api.getMe.mockResolvedValue({ userId: 'u', displayName: 'u', isSystemAdmin: false, mustChangePassword: false, groups: [], designer: { read: false, add: false, edit: false, delete: false } });
    api.getReport.mockResolvedValue(DETAIL(true));
    api.runReport.mockRejectedValue({ status: 500 });
    router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/reports/:id(\\d+)', component: ReportViewPage }] });
    await router.push('/reports/7');
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    mounted = mount({ template: '<q-layout><q-page-container><router-view /></q-page-container></q-layout>' }, {
      attachTo: document.body,
      global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, router, createPinia(), [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
    });
    await flushPromises();
    expect(q('run-error')).not.toBeNull();
    expect(q('run-retry')).not.toBeNull();
    expect(q('report-result')).toBeNull();
  });

  it('clicking a group row opens the drill dialog for its keys', async () => {
    await mountPage(DETAIL(true));
    api.drillReport.mockResolvedValue({ ...RESULT, truncated: false, groups: null, columns: [{ key: 'main:5:', label: '單號', role: 'field', valueType: 'text' }], detailRows: [['SO-1']] });
    q('group-row-0')!.click();
    await flushPromises();
    expect(api.drillReport).toHaveBeenCalledWith(7, ['a'], null, 'zh-TW');
    expect(q('report-detail-table')!.textContent).toContain('SO-1');
  });

  it('condition summary uses catalog field names and option labels, marks disabled options', async () => {
    const def = { ...DEF, filters: [
      { field: { source: 'main', fieldId: 5 }, op: 'in', value: ['opt_1', 'opt_2'] },
      { field: { source: 'main', fieldId: 0, viaFieldId: 6 }, op: 'eq', value: 'ACME' },
      { field: { source: 'main', fieldId: 99 }, op: 'eq', value: 'x' },
      { field: { source: 'main', fieldId: 4 }, op: 'eq', hidden: true },
    ] };
    await mountPage({ ...DETAIL(true), definition: def });
    expect(api.getReportCatalog).toHaveBeenCalledWith('orders', 'zh-TW');
    q('report-conditions')!.querySelector('.q-item')!.dispatchEvent(new Event('click'));
    await flushPromises();
    const texts = [...document.body.querySelectorAll('[data-testid="condition"]')].map((e) => e.textContent!.trim());
    expect(texts[0]).toBe('狀態 是其中之一 進行中, 舊狀態（已停用）');
    expect(texts[1]).toBe('客戶．標題 等於 ACME');
    expect(texts[2]).toBe('欄位 #99 等於 x');
    expect(q('condition-hidden')!.textContent!.trim()).toBe('隱藏條件');
  });
});
