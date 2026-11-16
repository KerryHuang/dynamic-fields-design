import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModuleCheck } from 'src/domain/dynamic/events-ledger';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { findInputByTestId } from 'src/test-utils/find-by-testid';
import { dynamicApi } from 'src/services/api/dynamic-api';
import CheckEditorDialog from './CheckEditorDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { createCheck: vi.fn(), updateCheck: vi.fn(), getModule: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, type: FieldType, extra: Partial<DesignerField['config']> = {}): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status: 'Active', options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, ...extra },
});
const mod = (key: string, fields: DesignerField[]): ModuleDetail => ({
  id: 1, key, programCode: 'P', name: { 'zh-TW': key }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields }],
});
const move = mod('move', [
  field(51, 'reference', { refTarget: { kind: 'module', key: 'item' } }), field(52, 'text'), field(53, 'decimal'), field(55, 'integer'), field(56, 'currency'), field(57, 'autonumber'),
]);
const saved: ModuleCheck = {
  key: 'stock', name: { 'zh-TW': '庫存' }, moduleKey: 'move', active: true,
  definition: { groupBy: [51], function: 'sum', fieldId: 55, assert: { op: 'gte', limit: { const: 0 } }, message: { 'zh-TW': '庫存不足' } },
};

const mounted: { unmount: () => void }[] = [];
async function open(check?: ModuleCheck) {
  api.getModule.mockResolvedValue(mod('item', [field(91, 'decimal'), field(92, 'text')]));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(CheckEditorDialog, {
    props: { modelValue: true, module: move, check },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  });
  mounted.push(w);
  await flushPromises();
  return w;
}
const $ = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const type = async (selector: string, value: string) => {
  const el = document.body.querySelector(selector) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
};
const comp = (w: Awaited<ReturnType<typeof open>>, id: string) => findInputByTestId(w, id);
const emit = async (w: Awaited<ReturnType<typeof open>>, id: string, v: unknown) => { comp(w, id).vm.$emit('update:modelValue', v); await flushPromises(); };
const saveBtn = () => $('check-save') as HTMLButtonElement;

describe('CheckEditorDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const w of mounted.splice(0)) w.unmount();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove()); // not the whole body: Quasar's notification container lives there once created
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('cannot be saved until the key, names, limit and message are filled', async () => {
    const w = await open();
    expect(saveBtn().disabled).toBe(true);
    await type('[data-testid="check-key"] input', 'stock');
    await type('[data-testid="i18n-zh-TW"] input', '庫存');
    await emit(w, 'check-function', 'count');
    await emit(w, 'check-limit-const', 0);
    expect(saveBtn().disabled).toBe(true); // message still missing
  });

  it('creates a check from the filled form and tells about notices', async () => {
    api.createCheck.mockResolvedValue({ ...saved, notices: ['已自動啟用可篩選：欄51'] });
    const w = await open();
    await type('[data-testid="check-key"] input', 'stock');
    await type('[data-testid="check-name"] [data-testid="i18n-zh-TW"] input', '庫存');
    await emit(w, 'check-group-by', [51, 52]);
    await emit(w, 'check-function', 'sum');
    await emit(w, 'check-field', 55);
    await emit(w, 'check-op', 'gte');
    await emit(w, 'check-limit-const', 0);
    await type('[data-testid="check-message"] [data-testid="i18n-zh-TW"] input', '庫存不足');
    saveBtn().click();
    await flushPromises();
    expect(api.createCheck).toHaveBeenCalledWith({
      key: 'stock', name: { 'zh-TW': '庫存' }, moduleKey: 'move', active: true,
      definition: { groupBy: [51, 52], function: 'sum', fieldId: 55, assert: { op: 'gte', limit: { const: 0 } }, message: { 'zh-TW': '庫存不足' } },
    });
    await vi.waitFor(() => expect(document.body.querySelector('.q-notifications')?.textContent).toContain('已自動啟用可篩選：欄51'));
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual([false]);
  });

  it('only offers indexable fields for grouping and number fields for the sum', async () => {
    const w = await open();
    const group = comp(w, 'check-group-by').props('options') as { value: number }[];
    expect(group.map((o) => o.value)).toEqual([51, 52, 55, 57]);
    const sum = comp(w, 'check-field').props('options') as { value: number }[];
    expect(sum.map((o) => o.value)).toEqual([53, 55, 56]);
  });

  it('edits an existing check: the code is fixed, the saved values are shown, save goes to PUT', async () => {
    api.updateCheck.mockResolvedValue(saved);
    const w = await open(saved);
    expect(($('check-key')!.querySelector('input') as HTMLInputElement).readOnly).toBe(true);
    expect(comp(w, 'check-field').props('modelValue')).toBe(55);
    saveBtn().click();
    await flushPromises();
    expect(api.updateCheck).toHaveBeenCalledWith('stock', saved);
  });

  it('limit through a reference picks a field of the referenced module', async () => {
    api.updateCheck.mockResolvedValue(saved);
    const w = await open(saved);
    await emit(w, 'check-limit-kind', 'via');
    await emit(w, 'check-limit-via', 51);
    expect(api.getModule).toHaveBeenCalledWith('item');
    expect((comp(w, 'check-limit-via-field').props('options') as { value: number }[]).map((o) => o.value)).toEqual([91]);
    await emit(w, 'check-limit-via-field', 91);
    saveBtn().click();
    await flushPromises();
    expect(api.updateCheck.mock.calls[0]![1].definition.assert.limit).toEqual({ via: 51, field: 91 });
  });

  it('puts server messages next to the input they are about', async () => {
    api.updateCheck.mockRejectedValue({ status: 400, errors: { 'definition.groupBy[0]': ['不能用這個欄位分組'], 'definition.where': ['條件不支援欄位比較'] } });
    await open(saved);
    saveBtn().click();
    await flushPromises();
    expect($('check-group-by-wrap')!.textContent).toContain('不能用這個欄位分組');
    expect(document.body.textContent).toContain('條件不支援欄位比較');
  });
});
