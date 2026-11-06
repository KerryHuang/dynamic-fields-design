import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import type { DynamicRecord, SchemaDetail, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { DetailRowState } from './detail-rows';
import DetailGrid from './DetailGrid.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getRecord: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false };
const item: SchemaField = {
  id: 1, key: 'f_1', type: 'reference', label: { 'zh-TW': '品項' }, options: [],
  config: { ...base, refTarget: { kind: 'module', key: 'item' }, fill: { mode: 'default', map: [{ from: 41, to: 2 }, { from: 42, to: 3 }] } },
  reference: { kind: 'module', key: 'item', name: { 'zh-TW': '品項' }, lightUrl: '/modules/item/light' },
};
const name: SchemaField = { id: 2, key: 'f_2', type: 'text', label: { 'zh-TW': '品名' }, options: [], config: base };
const price: SchemaField = { id: 3, key: 'f_3', type: 'decimal', label: { 'zh-TW': '單價' }, options: [], config: { ...base, readOnly: true } };
const detail: SchemaDetail = {
  moduleKey: 'line', name: { 'zh-TW': '明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_2', fields: [item, name, price], rules: [],
};
const row = (values: Record<string, unknown>): DetailRowState => ({ uid: 'u1', values, deleted: false });
const source = { id: 4, values: { f_41: 'Widget', f_42: 12.5 }, references: {} } as unknown as DynamicRecord;

function mountGrid(rows: DetailRowState[]) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(DetailGrid, {
    props: {
      detail, modelValue: rows, readonly: false, errors: new Map(),
      'onUpdate:modelValue': (v: DetailRowState[]) => w.setProps({ modelValue: v }),
    },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  return w;
}
const rowsOf = (w: ReturnType<typeof mountGrid>) => w.props('modelValue') as DetailRowState[];

describe('DetailGrid fill', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('picking an item fills the blank cells of that row only', async () => {
    api.getRecord.mockResolvedValue(source);
    const w = mountGrid([row({ f_1: null, f_2: null, f_3: null }), { ...row({ f_1: null, f_2: 'keep', f_3: null }), uid: 'u2' }]);
    w.findAllComponents(PopupSelect)[0]!.vm.$emit('update:modelValue', 4);
    await flushPromises();
    expect(api.getRecord).toHaveBeenCalledWith(4);
    expect(rowsOf(w)[0]!.values).toEqual({ f_1: 4, f_2: 'Widget', f_3: 12.5 });
    expect(rowsOf(w)[1]!.values).toEqual({ f_1: null, f_2: 'keep', f_3: null });
  });

  it('default mode leaves a value the user typed alone', async () => {
    api.getRecord.mockResolvedValue(source);
    const w = mountGrid([row({ f_1: null, f_2: 'typed', f_3: null })]);
    w.findComponent(PopupSelect).vm.$emit('update:modelValue', 4);
    await flushPromises();
    expect(rowsOf(w)[0]!.values).toEqual({ f_1: 4, f_2: 'typed', f_3: 12.5 });
  });

  it('a failed lookup leaves the picked value and the rest as they were', async () => {
    api.getRecord.mockRejectedValue({ status: 403 });
    const w = mountGrid([row({ f_1: null, f_2: null, f_3: null })]);
    w.findComponent(PopupSelect).vm.$emit('update:modelValue', 4);
    await flushPromises();
    expect(rowsOf(w)[0]!.values).toEqual({ f_1: 4, f_2: null, f_3: null });
  });
});
