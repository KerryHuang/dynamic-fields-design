import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { ModuleSchema, SchemaField, ViewSort } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import SortEditor from './SortEditor.vue';

const field = (id: number, label: string, sortable = true): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: sortable },
});
const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區塊' }, collapsed: false, fields: [field(1, 'A'), field(2, 'B'), field(3, 'C'), field(4, 'D'), field(5, 'E', false)] }],
};

function mountEditor(modelValue: ViewSort[]) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(SortEditor, {
    props: { schema, modelValue },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

describe('SortEditor', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('prevents duplicate field and caps at 3', async () => {
    const w = mountEditor([{ field: 1, order: 'asc' }]);
    // second row must not offer field 1; only sortable fields are listed
    await w.get('[data-testid="add-sort"]').trigger('click');
    let emitted = w.emitted('update:modelValue')!;
    expect(emitted[0]).toEqual([[{ field: 1, order: 'asc' }, { field: 2, order: 'asc' }]]);
    await w.setProps({ modelValue: [{ field: 1, order: 'asc' }, { field: 2, order: 'asc' }] });
    const fieldSelects = w.findAllComponents(GenericSelect).filter((_, i) => i % 2 === 0);
    const secondOptions = fieldSelects[1]!.props('options') as { value: string }[];
    expect(secondOptions.map((o) => o.value)).toEqual(['2', '3', '4']);

    await w.setProps({ modelValue: [{ field: 1, order: 'asc' }, { field: 2, order: 'asc' }, { field: 3, order: 'desc' }] });
    expect(w.get('[data-testid="add-sort"]').attributes('disabled')).toBeDefined();
    emitted = w.emitted('update:modelValue')!;
    expect(emitted).toHaveLength(1);
  });
});
