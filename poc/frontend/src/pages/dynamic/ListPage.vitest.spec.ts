import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ListView, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import FilterPanel from 'src/presentation/dynamic/list/FilterPanel.vue';
import ExportMenu from 'src/presentation/dynamic/list/ExportMenu.vue';
import ImportDialog from 'src/presentation/dynamic/list/ImportDialog.vue';
import ViewMenu from 'src/presentation/dynamic/list/ViewMenu.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ListPage from './ListPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getViews: vi.fn(), getPagedRecords: vi.fn(),
    getMe: vi.fn(), createView: vi.fn(), updateView: vi.fn(), deleteView: vi.fn(),
    exportRecords: vi.fn(), previewImport: vi.fn(), commitImport: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string, o: { sortable?: boolean; showInList?: boolean } = {}): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: o.showInList ?? true, isFilterable: true, isSortable: o.sortable ?? true },
});
const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), field(2, '甲'), field(3, '乙')] }],
};
const view = (p: Partial<ListView>): ListView => ({
  id: 9, name: { 'zh-TW': '我的檢視' }, shared: false, isOwner: true, columns: [2], sort: [], isDefault: false, staleFieldIds: [], ...p,
});
const row = { id: 1, moduleKey: 'm', title: '列一', values: {}, ownerUserId: 'a', createdUserId: 'a', createdTime: '2026-01-01T00:00:00+08:00', modifiedUserId: 'a', modifiedTime: '2026-01-01T00:00:00+08:00', rowVersion: 'v', references: {}, hostKey: null };
const lastCall = () => api.getPagedRecords.mock.calls.at(-1)![1];

let router: ReturnType<typeof createRouter>;
let schemaPatch: Partial<ModuleSchema> = {};

async function mountPage(views: ListView[] = [], queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }), slowViews = false, forbidden = false) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockImplementation(async (key) => {
    if (forbidden) throw { status: 403 };
    return { ...schema, ...schemaPatch, key };
  });
  api.getViews.mockImplementation(async () => {
    if (slowViews) await new Promise((r) => setTimeout(r, 30));
    return views;
  });
  api.getPagedRecords.mockResolvedValue({ items: [], totalCount: 100, currentPage: 1, pagesSize: 20 });
  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey', component: ListPage }] });
  await router.push('/dynamic/m');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  // q-page needs a surrounding layout
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(ListPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify, Dialog: quasar.Dialog } }],
        router, i18n, [VueQueryPlugin, { queryClient }]],
    },
  });
  await flushPromises();
  return w;
}

describe('ListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    schemaPatch = {};
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('resets page to 1 when filter changes', async () => {
    const w = await mountPage();
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 2, rowsPerPage: 20 });
    await flushPromises();
    expect(lastCall().currentPage).toBe(2);
    w.findComponent(FilterPanel).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    expect(lastCall().currentPage).toBe(1);
    expect(lastCall().filter).toBe('{"all":[{"field":2,"op":"eq","value":"x"}]}');
  });

  it('shows stale notice', async () => {
    const w = await mountPage([view({ isDefault: true, staleFieldIds: [7, 8] })]);
    expect(w.text()).toContain('此檢視有 2 個欄位已停用或不再開放篩選、排序，已略過');
  });

  it('header click replaces sort with single column', async () => {
    const w = await mountPage([view({ isDefault: true, columns: [2, 3], sort: [{ field: 2, order: 'asc' }, { field: 3, order: 'desc' }] })]);
    expect(lastCall().sort).toEqual([{ field: 2, order: 'asc' }, { field: 3, order: 'desc' }]);
    const headers = w.findAll('th.sortable');
    expect(headers.length).toBe(3);
    await headers[2]!.trigger('click');
    await flushPromises();
    expect(lastCall().sort).toEqual([{ field: 3, order: 'asc' }]);
    expect(lastCall().currentPage).toBe(1);
  });

  it('initialises from refetched views, not from the cached ones', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const stale = view({ isDefault: true, columns: [2], filter: { all: [{ field: 2, op: 'eq', value: 'x' }, { field: 3, op: 'eq', value: 'y' }] } });
    qc.setQueryData(['views', 'm'], [stale]);
    const fresh = view({ isDefault: true, columns: [2], filter: { all: [{ field: 3, op: 'eq', value: 'y' }] }, staleFieldIds: [2] });
    await mountPage([fresh], qc, true);
    await new Promise((r) => setTimeout(r, 80));
    await flushPromises();
    expect(api.getViews).toHaveBeenCalled();
    expect(api.getPagedRecords.mock.calls.length).toBeGreaterThan(0);
    for (const [, q] of api.getPagedRecords.mock.calls) expect(q.filter).toBe('{"all":[{"field":3,"op":"eq","value":"y"}]}');
  });

  it('ListPage shows forbidden notice on 403', async () => {
    const w = await mountPage([], undefined, false, true);
    expect(w.text()).toContain(zhTW.dynamic.forbidden);
    expect(w.text()).not.toContain(zhTW.dynamic.loadFailed);
  });

  it('ListPage hides the new button without add', async () => {
    const w = await mountPage();
    expect(w.find('[data-testid="new-btn"]').exists()).toBe(true);
    schemaPatch = { permissions: { ...schema.permissions, add: false } };
    const w2 = await mountPage();
    expect(w2.find('[data-testid="new-btn"]').exists()).toBe(false);
  });

  it('ListPage shows the closed-app banner only when the schema is readOnly', async () => {
    expect((await mountPage()).find('[data-testid="closed-banner"]').exists()).toBe(false);
    schemaPatch = { readOnly: true, permissions: { ...schema.permissions, add: false, edit: false, delete: false } };
    const w = await mountPage();
    expect(w.find('[data-testid="closed-banner"]').text()).toContain(zhTW.dynamic.closedBanner);
    expect(w.find('[data-testid="new-btn"]').exists()).toBe(false);
  });

  it('ListPage shows the import button only with add, and opens the import dialog', async () => {
    const w = await mountPage();
    expect(w.findComponent(ImportDialog).props('modelValue')).toBe(false);
    await w.get('[data-testid="import-btn"]').trigger('click');
    expect(w.findComponent(ImportDialog).props('modelValue')).toBe(true);
    schemaPatch = { permissions: { ...schema.permissions, add: false } };
    const w2 = await mountPage();
    expect(w2.find('[data-testid="import-btn"]').exists()).toBe(false);
  });

  it('ListPage hides the import button for extension modules', async () => {
    schemaPatch = { kind: 'extension', hostEntityKey: 'design.mold', titleFieldKey: null };
    const w = await mountPage();
    expect(w.find('[data-testid="new-btn"]').exists()).toBe(true);
    expect(w.find('[data-testid="import-btn"]').exists()).toBe(false);
    expect(w.findComponent(ImportDialog).exists()).toBe(false);
  });

  it('ListPage hands the export menu the current filter, sort and keyword', async () => {
    const w = await mountPage([view({ isDefault: true, columns: [2], sort: [{ field: 2, order: 'desc' }], filter: { all: [{ field: 2, op: 'eq', value: 'x' }] } })]);
    expect(w.findComponent(ExportMenu).props('query')).toEqual({
      filter: '{"all":[{"field":2,"op":"eq","value":"x"}]}', sort: [{ field: 2, order: 'desc' }], q: undefined,
    });
  });

  it('renders without a title column when titleFieldKey is null', async () => {
    schemaPatch = { titleFieldKey: null, blocks: [{ ...schema.blocks[0]!, fields: [field(2, '甲'), field(3, '乙')] }] };
    const w = await mountPage();
    expect(w.text()).not.toContain(zhTW.dynamic.loadFailed);
    expect(w.findAll('th').map((h) => h.text().replace('arrow_upward', ''))).toEqual(['甲', '乙', zhTW.dynamic.modifiedTime]);
  });

  it('notifies forbidden when saving a shared view returns 403', async () => {
    const w = await mountPage();
    api.createView.mockRejectedValueOnce({ status: 403 });
    w.findComponent(ViewMenu).vm.$emit('saveAs', { name: { 'zh-TW': 'v' }, shared: true, isDefault: false });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.textContent).toContain(zhTW.dynamic.forbidden);
  });

  it('title column header click sorts by the title field', async () => {
    const w = await mountPage();
    const headers = w.findAll('th.sortable');
    expect(headers.length).toBe(3);
    await headers[0]!.trigger('click');
    await flushPromises();
    expect(lastCall().sort).toEqual([{ field: 1, order: 'asc' }]);
  });

  it('notifies the first 400 message and keeps previous rows', async () => {
    const w = await mountPage();
    api.getPagedRecords.mockResolvedValue({ items: [row], totalCount: 1, currentPage: 1, pagesSize: 20 });
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 1, rowsPerPage: 20, sortBy: null });
    w.findComponent(FilterPanel).vm.$emit('update:modelValue', { all: [{ field: 3, op: 'eq', value: 'ok' }] });
    await flushPromises();
    expect(w.text()).toContain('列一');
    api.getPagedRecords.mockRejectedValueOnce({ status: 400, errors: { filter: ['欄位 2 未開放篩選'] } });
    w.findComponent(FilterPanel).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.textContent).toContain('欄位 2 未開放篩選');
    expect(w.text()).not.toContain(zhTW.dynamic.loadFailed);
    expect(w.text()).toContain('列一');
  });

  it('switching modules carries no old filter, sort or page and resets state', async () => {
    const w = await mountPage();
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 3, rowsPerPage: 20 });
    w.findComponent(FilterPanel).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    await w.get('[data-testid="keyword"]').setValue('abc');
    await new Promise((r) => setTimeout(r, 400));
    await flushPromises();
    expect(lastCall().filter).toBeDefined();
    expect(lastCall().q).toBe('abc');
    api.getPagedRecords.mockClear();
    await router.push('/dynamic/n');
    await flushPromises();
    const forN = api.getPagedRecords.mock.calls.filter(([k]) => k === 'n');
    expect(forN.length).toBeGreaterThan(0);
    for (const [, q] of forN) {
      expect(q.filter).toBeUndefined();
      expect(q.q).toBeUndefined();
      expect(q.sort).toEqual([]);
      expect(q.currentPage).toBe(1);
    }
    expect(api.getPagedRecords.mock.calls.every(([k]) => k === 'n')).toBe(true);
    expect((w.get('[data-testid="keyword"]').element as HTMLInputElement).value).toBe('');
  });

  it('reference column shows title from references', async () => {
    const refField: SchemaField = {
      ...field(4, '客戶'), type: 'reference',
      reference: { kind: 'module', key: 'cust', name: { 'zh-TW': 'x' }, lightUrl: '/x' },
    };
    schemaPatch = { blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), refField] }] };
    const w = await mountPage([view({ isDefault: true, columns: [4] })]);
    api.getPagedRecords.mockResolvedValue({
      items: [
        { ...row, id: 1, values: { f_4: 42 }, references: { f_4: { key: '42', title: '客戶甲', state: 'ok' } } },
        { ...row, id: 2, values: { f_4: 43 }, references: { f_4: { key: '43', state: 'missing' } } },
      ], totalCount: 2, currentPage: 1, pagesSize: 20,
    });
    w.findComponent(FilterPanel).vm.$emit('update:modelValue', undefined);
    await w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 1, rowsPerPage: 20, sortBy: 'f_4', descending: false });
    await flushPromises();
    const cells = w.findAll('tbody tr').map((tr) => tr.text());
    expect(cells[0]).toContain('客戶甲');
    expect(cells[1]).toContain(zhTW.dynamic.reference.missing);
  });

  it('extension module list shows host title column', async () => {
    schemaPatch = { kind: 'extension', hostEntityKey: 'design.mold', titleFieldKey: null };
    const w = await mountPage();
    api.getPagedRecords.mockResolvedValue({ items: [{ ...row, title: 'M-0001 外殼上蓋模' }], totalCount: 1, currentPage: 1, pagesSize: 20 });
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 2, rowsPerPage: 20 });
    await flushPromises();
    expect(w.find('tbody tr').text()).toContain('M-0001 外殼上蓋模');
  });
});
