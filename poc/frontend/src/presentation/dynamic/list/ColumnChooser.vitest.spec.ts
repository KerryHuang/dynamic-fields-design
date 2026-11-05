import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import ColumnChooser from './ColumnChooser.vue';

const field = (id: number, label: string): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false },
});
const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區塊' }, collapsed: false, columns: 2, fields: [field(1, 'T'), field(2, 'B'), field(3, 'C'), field(4, 'D')] }],
};

function mountChooser(modelValue: number[]) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(ColumnChooser, { props: { schema, modelValue }, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } });
}

describe('ColumnChooser', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('moves the right column when modelValue holds a non-candidate id', async () => {
    // 99 is not in the schema; visible order is [2, 3]
    const w = mountChooser([99, 2, 3]);
    await w.findAll('button[aria-label="下移"]')[0]!.trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([[99, 3, 2]]);
  });

  it('cannot remove the last visible column', async () => {
    const w = mountChooser([99, 2]);
    const box = w.findComponent(quasar.QCheckbox);
    expect(box.props('disable')).toBe(true);
    box.vm.$emit('update:modelValue', false);
    expect(w.emitted('update:modelValue')).toBeUndefined();
  });
});
