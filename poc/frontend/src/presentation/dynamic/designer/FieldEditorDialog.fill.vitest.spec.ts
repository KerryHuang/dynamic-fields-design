import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import FieldEditorDialog from './FieldEditorDialog.vue';
import FieldMapEditor from './FieldMapEditor.vue';

const base = { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false };
const mk = (id: number, type: FieldType, label: string, config: Partial<DesignerField['config']> = {}): DesignerField =>
  ({ id, key: `f_${id}`, type, label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active', options: [], config: { ...base, ...config } });
const moduleOf = (key: string, fields: DesignerField[]): ModuleDetail => ({
  id: 1, key, programCode: 'P', name: { 'zh-TW': key }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields }],
});

const customerFields = [mk(31, 'text', '客戶名稱'), mk(32, 'text', '付款條件'), mk(33, 'enum', '等級')];
const ownFields = [mk(2, 'text', '客戶名稱', { readOnly: true }), mk(3, 'text', '付款條件'), mk(4, 'formula', '公式'), mk(9, 'reference', '客戶', { refTarget: { kind: 'module', key: 'cust' } })];

function mountDialog(field: DesignerField) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(FieldEditorDialog, {
    props: { modelValue: true, moduleKey: 'order', blockId: 1, field },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
}
const save = () => [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();

describe('FieldEditorDialog fill', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    vi.spyOn(dynamicApi, 'getModule').mockImplementation(async (k) => (k === 'cust' ? moduleOf('cust', customerFields) : moduleOf('order', ownFields)));
    vi.spyOn(dynamicApi, 'getModules').mockResolvedValue([
      { id: 1, key: 'cust', programCode: 'P1', name: { 'zh-TW': '客戶' }, status: 'Active', version: 1, kind: 'standalone' },
    ] as never);
    vi.spyOn(dynamicApi, 'entities').mockResolvedValue([]);
  });
  afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = ''; });

  it('keeps an existing fill when the field is saved again', async () => {
    const fill = { mode: 'lock' as const, map: [{ from: 31, to: 2 }] };
    const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
    const w = mountDialog({ ...ownFields[3]!, config: { ...ownFields[3]!.config, fill } });
    await flushPromises();
    expect(document.body.querySelector('[data-testid="fill-settings"]')).toBeTruthy();
    save();
    await flushPromises();
    expect(spy.mock.calls[0]![1].config.fill).toEqual(fill);
    w.unmount();
  });

  it('offers the referenced module fields (no enums) on the left and own writable fields (no formulas, not itself) on the right', async () => {
    const w = mountDialog(ownFields[3]!);
    await flushPromises();
    const editor = w.findComponent(FieldMapEditor);
    expect((editor.props('fromOptions') as { value: number }[]).map((o) => o.value)).toEqual([31, 32]);
    expect((editor.props('toOptions') as { value: number }[]).map((o) => o.value)).toEqual([2, 3]);
    w.unmount();
  });

  it('adds a mapping row and sends only complete rows; no rows means no fill', async () => {
    const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
    const w = mountDialog(ownFields[3]!);
    await flushPromises();
    save();
    await flushPromises();
    expect(spy.mock.calls[0]![1].config).not.toHaveProperty('fill');

    const editor = w.findComponent(FieldMapEditor);
    editor.vm.$emit('update:modelValue', [{ from: 32, to: 3 }, { from: 31, to: null }]);
    await flushPromises();
    save();
    await flushPromises();
    expect(spy.mock.calls[1]![1].config.fill).toEqual({ mode: 'default', map: [{ from: 32, to: 3 }] });
    w.unmount();
  });

  it('shows no fill section for a non-reference field', async () => {
    const w = mountDialog(ownFields[0]!);
    await flushPromises();
    expect(document.body.querySelector('[data-testid="fill-settings"]')).toBeNull();
    w.unmount();
  });
});
