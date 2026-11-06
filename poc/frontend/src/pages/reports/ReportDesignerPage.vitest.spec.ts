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
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import FilterValueInput from 'src/presentation/reports/designer/FilterValueInput.vue';
import ReportDesignerPage from './ReportDesignerPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getReportCatalogModules: vi.fn(), getReportCatalog: vi.fn(), previewReport: vi.fn(), createReport: vi.fn(), updateReport: vi.fn(),
    getReport: vi.fn(), getUsers: vi.fn(), getRecord: vi.fn(),
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
const detail = (def: ReportDefinition, isOwner = true): ReportDetail => ({ id: 7, name: '月報', ownerName: '我', isOwner, sharedGroupIds: [], mainModuleKey: 'orders', definition: def, canExport: true });

const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;
let router: ReturnType<typeof createRouter>;

async function mountAt(path: string, catalog: ModuleCatalog = CATALOG) {
  api.getReportCatalogModules.mockResolvedValue([{ key: 'orders', name: '訂單' }]);
  api.getReportCatalog.mockResolvedValue(catalog);
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

  it('the dedupe note comes only from the backend preview, the designer has no rule of its own', async () => {
    await mountAt('/reports/new');
    api.previewReport.mockResolvedValue({ ...RESULT, dedupedNote: true });
    await pick('main-module', 'orders');
    await pick('child-module', 'shipments:20');
    for (let i = 0; i < 3; i++) await click('designer-next');
    await pick('measure-field', 'main:2:');
    await click('measure-add');
    expect(q('deduped-note')).toBeNull();
    await click('designer-next');
    expect(q('deduped-note')).not.toBeNull();
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


const CAT2: ModuleCatalog = {
  fields: [
    ...CATALOG.fields,
    F(4, '狀態', { type: 'enum', options: [{ id: 'opt_1', label: '進行中', active: true }] }),
    F(7, '客戶', { type: 'reference', refKind: 'module', refKey: 'customers' }),
  ],
  via: [{ fieldId: 7, label: '客戶', targetKey: 'customers', fields: [F(10, '地區')] }],
  children: CATALOG.children,
};
const goTo = async (step: number) => { for (let i = 0; i < step; i++) await click('designer-next'); };
const keys = (list: { key: string }[]) => list.map((x) => x.key);

describe('ReportDesignerPage review fixes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('a saved lastMonth range can advance and save without N', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, primaryDateField: MAIN(3), dateRange: { kind: 'lastMonth', n: null } }));
    api.updateReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/7/edit');
    await goTo(4);
    expect((q('designer-save') as HTMLButtonElement).disabled).toBe(false);
    await click('designer-save');
    expect(api.updateReport.mock.calls[0]![2]).toMatchObject({ dateRange: { kind: 'lastMonth', n: null } });
  });

  it('ratio measures can be added, need a complete condition, and show the condition text', async () => {
    await mountAt('/reports/new', CAT2);
    await pick('main-module', 'orders');
    await goTo(3);
    await pick('measure-kind', 'ratio');
    await pick('ratio-field', 'main:2:');
    await click('measure-add');
    expect(q('measure-text-0')!.textContent).toContain('條件占比');
    expect(q('measure-text-0')!.textContent).toContain('金額');
    expect(q('measure-cond-0')).not.toBeNull();
    expect((q('designer-next') as HTMLButtonElement).disabled).toBe(true);
  });

  it('a saved ratio measure shows its condition value', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, measures: [{ id: 'm1', kind: 'ratio', field: null, condition: { field: MAIN(4), op: 'in', value: ['opt_1'] } }] }));
    await mountAt('/reports/7/edit', CAT2);
    await goTo(3);
    expect(q('measure-text-0')!.textContent).toBe('條件占比 狀態 是其中之一 進行中');
  });

  it('reference conditions use the record picker with the target light url and show stored titles', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, filters: [{ field: MAIN(7), op: 'in', value: [5, 6, 8] }] }));
    api.getRecord.mockImplementation(async (id: number) => {
      if (id === 5) return { id, title: 'ACME' } as never;
      throw { status: id === 6 ? 404 : 403 };
    });
    await mountAt('/reports/7/edit', CAT2);
    await goTo(2);
    const popup = mounted!.findComponent(PopupSelect);
    expect(popup.props()).toMatchObject({ lightUrl: '/modules/customers/records/light', valueKind: 'number', multiple: true });
    expect((q('popup-input') as HTMLInputElement).value).toBe('ACME、已失效、無權限檢視');
  });

  it('an entity reference uses the entity light url with string keys', async () => {
    const cat: ModuleCatalog = { ...CAT2, fields: [...CAT2.fields, F(8, '實體', { type: 'reference', refKind: 'entity', refKey: 'sales.customer' })] };
    api.getReport.mockResolvedValue(detail({ ...DEF, filters: [{ field: MAIN(8), op: 'eq', value: 'C001' }] }));
    await mountAt('/reports/7/edit', cat);
    await goTo(2);
    expect(mounted!.findComponent(PopupSelect).props()).toMatchObject({ lightUrl: '/entities/sales.customer/light', valueKind: 'string' });
    expect(api.getRecord).not.toHaveBeenCalled();
  });

  it('a via entity title condition and ratio use the picker with key operators, never free text', async () => {
    const title = F(0, '標題', { type: 'reference', refKind: 'entity', refKey: 'sales.customer' });
    const cat: ModuleCatalog = { ...CAT2, via: [{ fieldId: 8, label: '客戶', targetKey: 'sales.customer', fields: [title] }] };
    const via = { source: 'main', fieldId: 0, viaFieldId: 8 };
    api.getReport.mockResolvedValue(detail({
      ...DEF, filters: [{ field: via, op: 'ne', value: 'C-003' }],
      measures: [{ id: 'm1', kind: 'ratio', field: null, condition: { field: via, op: 'eq', value: 'C-001' } }],
    }));
    await mountAt('/reports/7/edit', cat);
    await goTo(2);
    expect(mounted!.findComponent(PopupSelect).props()).toMatchObject({ lightUrl: '/entities/sales.customer/light', valueKind: 'string', modelValue: 'C-003' });
    const ops = (comp('filter-0-op').props() as unknown as { options: { value: string }[] }).options.map((o) => o.value);
    expect(ops).toEqual(['eq', 'ne', 'in', 'notIn', 'isEmpty', 'isNotEmpty']);
    await goTo(1);
    expect(mounted!.findComponent(PopupSelect).props()).toMatchObject({ lightUrl: '/entities/sales.customer/light', modelValue: 'C-001' });
  });

  it('a saved sort key pointing at a missing field blocks until removed in the group step', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, sort: [{ key: 'main:99:', desc: false }] }));
    api.updateReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/7/edit');
    expect(q('designer-unavailable')).not.toBeNull();
    await goTo(3);
    expect(q('sort-unavailable')).not.toBeNull();
    expect((q('designer-next') as HTMLButtonElement).disabled).toBe(true);
    await click('sort-remove-0');
    expect(q('designer-unavailable')).toBeNull();
    await goTo(1);
    await click('designer-save');
    expect(api.updateReport.mock.calls[0]![2]).toMatchObject({ sort: [] });
  });

  it('unchecking a column drops the sort entry that points at it', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, columns: [MAIN(1), MAIN(3)], sort: [{ key: 'main:3:', desc: true }] }));
    api.updateReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/7/edit');
    await goTo(1);
    await click('field-main:3:');
    await goTo(3);
    await click('designer-save');
    expect(api.updateReport.mock.calls[0]![2]).toMatchObject({ columns: [MAIN(1)], sort: [] });
  });

  it('a saved date group with no bucket shows no bucket, a new date group stores month, and a field is grouped once', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, groups: [{ field: MAIN(3), bucket: null }], measures: [{ id: 'm1', kind: 'countMain', field: null, condition: null }] }));
    await mountAt('/reports/7/edit', { ...CATALOG, fields: [...CATALOG.fields, F(9, '交期', { type: 'date', isDate: true })] });
    await goTo(3);
    expect((comp('group-bucket-0').props() as unknown as { modelValue: string }).modelValue).toBe('');
    const options = () => (comp('group-add').props() as unknown as { options: { value: string }[] }).options.map((o) => o.value);
    expect(options()).not.toContain('main:3:');
    await pick('group-add', 'main:9:');
    expect((comp('group-bucket-1').props() as unknown as { modelValue: string }).modelValue).toBe('month');
    expect(options()).not.toContain('main:9:');
  });

  it('switching the report in the route reloads the designer state', async () => {
    api.getReport.mockImplementation(async (id: number) => ({ ...detail({ ...DEF, columns: [id === 5 ? MAIN(1) : MAIN(3)] }), id, name: `報表${id}` }));
    api.updateReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/5/edit');
    await router.push('/reports/6/edit');
    await flushPromises();
    await goTo(4);
    expect((comp('report-name-input').props() as unknown as { modelValue: string }).modelValue).toBe('報表6');
    await click('designer-save');
    expect(api.updateReport).toHaveBeenCalledWith(6, '報表6', expect.objectContaining({ columns: [MAIN(3)] }));
  });

  it('changing the main module asks first when there are settings, and keeps them on cancel', async () => {
    api.getReport.mockResolvedValue(detail({ ...DEF, columns: [MAIN(1)] }));
    api.getReportCatalogModules.mockResolvedValue([{ key: 'orders', name: '訂單' }, { key: 'tickets', name: '工單' }]);
    await mountAt('/reports/7/edit');
    api.getReportCatalogModules.mockResolvedValue([{ key: 'orders', name: '訂單' }, { key: 'tickets', name: '工單' }]);
    await pick('main-module', 'tickets');
    expect(q('main-change-confirm')).not.toBeNull();
    await click('main-change-cancel');
    await goTo(1);
    expect(q('field-main:1:')).not.toBeNull();
    await click('designer-prev');
    await pick('main-module', 'tickets');
    await click('main-change-confirm');
    expect(api.getReportCatalog).toHaveBeenLastCalledWith('tickets', 'zh-TW');
  });

  it('choosing a main module on an empty designer does not ask', async () => {
    await mountAt('/reports/new');
    await pick('main-module', 'orders');
    expect(q('main-change-confirm')).toBeNull();
  });

  it('deleting a middle condition row keeps the remaining rows in step with state', async () => {
    const eq = (v: string) => ({ field: MAIN(1), op: 'eq', value: v });
    api.getReport.mockResolvedValue(detail({ ...DEF, filters: [eq('A'), eq('B'), eq('C')] }));
    api.updateReport.mockResolvedValue(detail(DEF));
    await mountAt('/reports/7/edit');
    await goTo(2);
    const values = () => [...document.body.querySelectorAll<HTMLInputElement>('[data-testid="filter-value"] input')].map((i) => i.value);
    expect(values()).toEqual(['A', 'B', 'C']);
    await click('filter-1-remove');
    expect(values()).toEqual(['A', 'C']);
    expect(q('filter-2')).toBeNull();
    const last = document.body.querySelectorAll<HTMLInputElement>('[data-testid="filter-value"] input')[1]!;
    last.value = 'D';
    last.dispatchEvent(new Event('input'));
    await flushPromises();
    await goTo(2);
    await click('designer-save');
    expect(api.updateReport.mock.calls[0]![2].filters).toEqual([eq('A'), eq('D')]);
  });

  it('the reference picker renders without attribute fallthrough warnings', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    api.getReport.mockResolvedValue(detail({ ...DEF, filters: [{ field: MAIN(7), op: 'eq', value: 5 }] }));
    api.getRecord.mockResolvedValue({ id: 5, title: 'ACME' } as never);
    await mountAt('/reports/7/edit', CAT2);
    await goTo(2);
    expect(q('filter-value')!.firstElementChild!.className).toContain('col');
    expect(warn.mock.calls.filter((c) => String(c[0]).includes('Extraneous non-props'))).toEqual([]);
    warn.mockRestore();
  });

  it('saving a loaded report untouched sends exactly the loaded definition', async () => {
    const loaded: ReportDefinition = {
      version: 1, mainModuleKey: 'orders', child: { moduleKey: 'shipments', linkFieldId: 20 },
      columns: [MAIN(3), { source: 'main', fieldId: 10, viaFieldId: 7 }, { source: 'child', fieldId: 21, viaFieldId: null }],
      groups: [{ field: MAIN(3), bucket: 'month' }, { field: { source: 'main', fieldId: 10, viaFieldId: 7 }, bucket: null }],
      measures: [
        { id: 'm1', kind: 'sum', field: MAIN(2), condition: null },
        { id: 'm3', kind: 'ratio', field: null, condition: { field: MAIN(4), op: 'in', value: ['opt_1'] } },
      ],
      filters: [{ field: MAIN(2), op: 'between', value: [1, 9] }, { field: MAIN(7), op: 'eq', value: 5 }],
      primaryDateField: MAIN(3), dateRange: { kind: 'lastMonth', n: null },
      sort: [{ key: 'm1', desc: true }, { key: 'main:3:', desc: false }], viz: 'bar',
    };
    api.getReport.mockResolvedValue(detail(loaded));
    api.getRecord.mockResolvedValue({ id: 5, title: 'ACME' } as never);
    api.updateReport.mockResolvedValue(detail(loaded));
    await mountAt('/reports/7/edit', CAT2);
    await goTo(4);
    await click('designer-save');
    expect(api.updateReport).toHaveBeenCalledTimes(1);
    expect(api.updateReport.mock.calls[0]![2]).toEqual(loaded);
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

  it('datetime conditions show Taipei wall time for stored +08:00 and other offsets', async () => {
    api.getUsers.mockResolvedValue([]);
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const show = (value: unknown) => {
      const w = mount(FilterValueInput, {
        props: { field: F(6, '時間', { type: 'datetime' }), op: 'gt', modelValue: value },
        global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, createPinia(), [VueQueryPlugin, { queryClient: new QueryClient() }]] },
      });
      const v = w.find('input').element.value;
      w.unmount();
      return v;
    };
    expect(show('2026-11-01T08:30:00+08:00')).toBe('2026-11-01T08:30');
    expect(show('2026-11-01T00:30:00Z')).toBe('2026-11-01T08:30');
    expect(show('2026-11-01T08:30')).toBe('2026-11-01T08:30');
    expect(show('2026-11-01')).toBe('2026-11-01T00:00');
  });
});
