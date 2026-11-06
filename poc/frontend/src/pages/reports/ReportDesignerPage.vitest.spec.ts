import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ModuleCatalog, ReportDefinition, ReportDetail, ReportResult } from 'src/domain/dynamic/report-types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import FilterValueInput from 'src/presentation/reports/designer/FilterValueInput.vue';
import ReportDesignerPage from './ReportDesignerPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getReportCatalogModules: vi.fn(), getReportCatalog: vi.fn(), previewReport: vi.fn(), createReport: vi.fn(), updateReport: vi.fn(),
    getReport: vi.fn(), getUsers: vi.fn(),
  },
}));
vi.mock('src/presentation/reports/ReportChart.vue', () => ({ default: { template: '<i />' } }));
const api = vi.mocked(dynamicApi);

const F = (id: number, label: string, o: Partial<ModuleCatalog['fields'][number]> = {}) => ({
  id, label, type: 'text', canGroup: true, canSum: false, canMinMax: false, isDate: false, options: null, ...o,
});
const CATALOG: ModuleCatalog = {
  fields: [F(1, '客戶'), F(2, '金額', { type: 'currency', canGroup: false, canSum: true }), F(3, '訂單日', { type: 'date', isDate: true })],
  via: [],
  children: [{ moduleKey: 'shipments', name: '出貨', linkFieldId: 20, linkLabel: '訂單', isDetail: false, fields: [F(21, '數量', { canSum: true })], via: [] }],
};
const MAIN = (id: number) => ({ source: 'main', fieldId: id, viaFieldId: null });
const DEF: ReportDefinition = {
  version: 1, mainModuleKey: 'orders', child: null, columns: [MAIN(1)], groups: [], measures: [], filters: [],
  primaryDateField: null, dateRange: null, sort: [], viz: 'table',
};
const RESULT: ReportResult = { state: 'ok', rowMeaning: '一張訂單', basisDate: '2026-11-05', dedupedNote: false, columns: [], groups: [], total: {} };
const detail = (def: ReportDefinition, isOwner = true): ReportDetail => ({ id: 7, name: '月報', ownerName: '我', isOwner, sharedGroupIds: [], mainModuleKey: 'orders', definition: def });

const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;
let router: ReturnType<typeof createRouter>;

async function mountAt(path: string) {
  api.getReportCatalogModules.mockResolvedValue([{ key: 'orders', name: '訂單' }]);
  api.getReportCatalog.mockResolvedValue(CATALOG);
  api.getUsers.mockResolvedValue([]);
  api.previewReport.mockResolvedValue(RESULT);
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/reports', component: { template: '<i/>' } }, { path: '/reports/:id(\\d+)', component: { template: '<i/>' } },
      { path: '/reports/new', component: ReportDesignerPage }, { path: '/reports/:id(\\d+)/edit', component: ReportDesignerPage },
    ],
  });
  await router.push(path);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount({ template: '<q-layout><q-page-container><router-view /></q-page-container></q-layout>' }, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar }], i18n, router, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
}
const comp = (id: string) => [...mounted!.findAllComponents(quasar.QSelect), ...mounted!.findAllComponents(quasar.QInput)]
  .filter((c) => c.element.contains(q(id))).pop()!;
async function pick(id: string, value: unknown) {
  if (!q(id)) throw new Error(`missing ${id}: ${document.body.innerHTML.slice(0, 800)}`);
  comp(id).vm.$emit('update:modelValue', value);
  await flushPromises();
}
async function click(id: string) { q(id)!.click(); await flushPromises(); }

describe('ReportDesignerPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('next is disabled until a main module is chosen, then shows fields in three groups', async () => {
    await mountAt('/reports/new');
    expect((q('designer-next') as HTMLButtonElement).disabled).toBe(true);
    await pick('main-module', 'orders');
    expect(api.getReportCatalog).toHaveBeenCalledWith('orders', 'zh-TW');
    await pick('child-module', 'shipments:20');
    await click('designer-next');
    expect(q('fields-main')).not.toBeNull();
    expect(q('fields-child')!.textContent).toContain('出貨．數量');
  });

  it('dedupe note appears at the group step when merged and main sum', async () => {
    await mountAt('/reports/edit-less'.replace('edit-less', 'new'));
    await pick('main-module', 'orders');
    await pick('child-module', 'shipments:20');
    for (let i = 0; i < 3; i++) await click('designer-next');
    expect(q('dedupe-note')).toBeNull();
    await pick('measure-field', 'main:2:');
    await click('measure-add');
    expect(q('dedupe-note')).not.toBeNull();
  });

  it('measure fields follow the backend canSum flag', async () => {
    await mountAt('/reports/new');
    await pick('main-module', 'orders');
    for (let i = 0; i < 3; i++) await click('designer-next');
    const options = (comp('measure-field').props() as unknown as { options: { value: string }[] }).options;
    expect(options.map((o) => o.value)).toEqual(['main:2:']);
  });

  it('preview posts current definition and save posts and navigates', async () => {
    api.createReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/new');
    await pick('main-module', 'orders');
    await click('designer-next');
    await click('field-main:1:');
    await click('designer-next');
    await click('designer-next');
    await click('designer-next');
    expect(api.previewReport).toHaveBeenCalledTimes(1);
    expect(api.previewReport.mock.calls[0]![0]).toMatchObject({ version: 1, mainModuleKey: 'orders', columns: [MAIN(1)], child: null, sort: [], viz: 'table' });
    expect(q('report-result')).not.toBeNull();
    expect((q('designer-save') as HTMLButtonElement).disabled).toBe(true);
    await pick('report-name-input', '新報表');
    await click('viz-bar');
    await click('designer-save');
    expect(api.createReport).toHaveBeenCalledWith('新報表', expect.objectContaining({ mainModuleKey: 'orders', viz: 'bar' }));
    expect(router.currentRoute.value.path).toBe('/reports/7');
  });

  it('shows the backend 400 messages instead of swallowing them', async () => {
    api.createReport.mockRejectedValue({ status: 400, errors: { definition: ['設定日期區間需要選主要日期欄位'] } });
    await mountAt('/reports/new');
    await pick('main-module', 'orders');
    await click('designer-next');
    await click('field-main:1:');
    for (let i = 0; i < 3; i++) await click('designer-next');
    await pick('report-name-input', 'x');
    await click('designer-save');
    expect(q('save-errors')!.textContent).toContain('設定日期區間需要選主要日期欄位');
  });

  it('date range is disabled until a primary date field is chosen', async () => {
    await mountAt('/reports/new');
    await pick('main-module', 'orders');
    await click('designer-next');
    await click('designer-next');
    expect((comp('date-range').props() as unknown as { disable: boolean }).disable).toBe(true);
    await pick('primary-date', 'main:3:');
    expect((comp('date-range').props() as unknown as { disable: boolean }).disable).toBe(false);
  });

  it('definition referencing removed field shows warning and blocks save until removed', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, columns: [MAIN(1), MAIN(99)] }));
    api.updateReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/7/edit');
    expect(q('designer-unavailable')).not.toBeNull();
    await click('designer-next');
    expect(q('columns-unavailable')!.textContent).toContain('#99');
    expect((q('designer-next') as HTMLButtonElement).disabled).toBe(false);
    await click('designer-next');
    await click('designer-next');
    expect((q('designer-next') as HTMLButtonElement).disabled).toBe(true);
    await click('designer-prev');
    await click('designer-prev');
    await click('column-remove-1');
    expect(q('designer-unavailable')).toBeNull();
    for (let i = 0; i < 2; i++) await click('designer-next');
    await click('designer-next');
    await click('designer-save');
    expect(api.updateReport).toHaveBeenCalledWith(7, '月報', expect.objectContaining({ columns: [MAIN(1)] }));
  });

  it('a definition with masked conditions shows the reason and offers no wizard', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, filters: [{ field: MAIN(2), op: 'gt', hidden: true }] }));
    await mountAt('/reports/7/edit');
    expect(q('designer-hidden-blocked')).not.toBeNull();
    expect(q('designer-next')).toBeNull();
    expect(q('designer-save')).toBeNull();
    expect(api.updateReport).not.toHaveBeenCalled();
  });

  it('non-owner cannot edit', async () => {
    api.getReport.mockResolvedValue(detail(DEF, false));
    await mountAt('/reports/7/edit');
    expect(q('designer-not-owner')).not.toBeNull();
    expect(q('designer-next')).toBeNull();
  });
});

describe('FilterValueInput', () => {
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });
  it('inactive options are marked and cannot be newly picked, but stay selectable-as-shown', async () => {
    api.getUsers.mockResolvedValue([]);
    const field = F(5, '狀態', { type: 'enum', options: [{ id: 'opt_1', label: '進行中', active: true }, { id: 'opt_2', label: '舊狀態', active: false }] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(FilterValueInput, {
      props: { field, op: 'in', modelValue: ['opt_2'] },
      global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, createPinia(), [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    const opts = w.findComponent(quasar.QSelect).props('options') as { value: string; label: string; disable: boolean }[];
    expect(opts.find((o) => o.value === 'opt_2')).toMatchObject({ label: '舊狀態（已停用）', disable: true });
    expect(opts.find((o) => o.value === 'opt_1')!.disable).toBe(false);
    w.unmount();
  });
});
