import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { DynamicRecord, ModuleSchema, RecordConversion, SchemaField, SchemaWorkflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DetailPage from './DetailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn(), getRecordConversions: vi.fn(), convertRecord: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true };
const status: SchemaField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, config: base,
  options: [11, 12, 13].map((n) => ({ value: `opt_${n}`, label: { 'zh-TW': `s${n}` }, active: true })),
};
const workflow: SchemaWorkflow = {
  statusFieldKey: 'f_5', initialState: 'opt_11',
  states: [{ value: 'opt_11', readOnly: false }, { value: 'opt_12', readOnly: true }, { value: 'opt_13', readOnly: true }],
  transitions: [{ from: 'opt_12', to: 'opt_13' }],
};
const schema = (extra: Partial<ModuleSchema> = {}): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [],
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [
    { id: 1, key: 'f_1', type: 'text', label: { 'zh-TW': '標題' }, options: [], config: base }, status,
  ] }], ...extra,
});
const record = (state: string): DynamicRecord => ({
  id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五', f_5: state }, ownerUserId: 'a', createdUserId: 'a', createdTime: '',
  modifiedUserId: 'a', modifiedTime: '', rowVersion: 'rv', references: {}, hostKey: null,
});

async function mountPage(s: ModuleSchema, rec: DynamicRecord, conversions: RecordConversion[] = []) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(s);
  api.getRecord.mockResolvedValue(rec);
  api.related.mockResolvedValue([]);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  api.getRecordConversions.mockResolvedValue(conversions);
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

const conversion = (extra: Partial<RecordConversion> = {}): RecordConversion => ({
  key: 'q2o', name: { 'zh-TW': '報價轉訂單' }, targetModuleKey: 'order', targetName: { 'zh-TW': '訂單' }, allowed: true, targets: [], ...extra,
});

describe('DetailPage document flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('a read-only state hides delete, and edit when the status cannot advance', async () => {
    const stuck = await mountPage(schema({ workflow }), record('opt_13'));
    expect(stuck.text()).not.toContain(zhTW.dynamic.delete);
    expect(stuck.text()).not.toContain(zhTW.dynamic.edit);
    const movable = await mountPage(schema({ workflow }), record('opt_12'));
    expect(movable.text()).not.toContain(zhTW.dynamic.delete);
    expect(movable.text()).toContain(zhTW.dynamic.edit);
  });

  it('an editable state keeps both buttons', async () => {
    const w = await mountPage(schema({ workflow }), record('opt_11'));
    expect(w.text()).toContain(zhTW.dynamic.delete);
    expect(w.text()).toContain(zhTW.dynamic.edit);
  });

  it('shows a convert button only for allowed conversions, and the reason for the others', async () => {
    const w = await mountPage(schema(), record('opt_11'), [
      conversion(),
      conversion({ key: 'q2x', name: { 'zh-TW': '報價轉出貨' }, allowed: false, reason: '來源狀態不符' }),
    ]);
    expect(w.find('[data-testid="convert-open-q2o"]').exists()).toBe(true);
    expect(w.find('[data-testid="convert-open-q2x"]').exists()).toBe(false);
    expect(w.find('[data-testid="convert-reason-q2x"]').text()).toContain('來源狀態不符');
  });

  it('lists the documents already converted out, linked to their records', async () => {
    const w = await mountPage(schema(), record('opt_11'), [conversion({ targets: [{ id: 9, title: 'SO-0009', moduleKey: 'order' }] })]);
    const link = w.find('[data-testid="convert-targets-q2o"] a');
    expect(link.text()).toBe('SO-0009');
    expect(link.attributes('href')).toBe('/dynamic/order/9');
  });

  it('shows nothing about conversions when there are none', async () => {
    const w = await mountPage(schema(), record('opt_11'), []);
    expect(w.find('[data-testid="record-conversions"]').exists()).toBe(false);
  });
});
