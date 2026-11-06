import { mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { ConditionNode, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import FilterPanel from './FilterPanel.vue';

const field = (id: number, type: SchemaField['type'], label: string, filterable = true): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: filterable, isSortable: false },
});

const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{
    id: 1, label: { 'zh-TW': '區塊' }, collapsed: false, columns: 2,
    fields: [field(1, 'text', '名稱'), field(2, 'decimal', '金額'), field(3, 'text', '備註', false), field(4, 'autonumber', '編號')],
  }],
};

function mountPanel(modelValue?: ConditionNode, panelSchema: ModuleSchema = schema) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(FilterPanel, {
    props: { schema: panelSchema, modelValue },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n] },
  });
}

describe('FilterPanel', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('explains instead of offering conditions when no field is filterable', () => {
    const none: ModuleSchema = { ...schema, blocks: [{ ...schema.blocks[0]!, fields: [field(3, 'text', '備註', false)] }] };
    const w = mountPanel(undefined, none);
    expect(w.find('[data-testid="no-filter-fields"]').text()).toContain('可篩選');
    expect(w.findComponent({ name: 'ConditionEditor' }).exists()).toBe(false);
  });

  it('lists only filterable fields', () => {
    const w = mountPanel({ all: [{ field: 1, op: 'eq', value: 'x' }] });
    const options = w.findAllComponents(GenericSelect)[1]!.props('options') as { value: string; label: string }[];
    expect(options.map((o) => o.label)).toEqual(['名稱', '金額', '編號']);
  });

  it('changing operator shape clears value', async () => {
    const w = mountPanel({ all: [{ field: 1, op: 'eq', value: 'x' }] });
    w.findAllComponents(GenericSelect)[2]!.vm.$emit('update:modelValue', 'isEmpty');
    await w.vm.$nextTick();
    await w.get('[data-testid="apply"]').trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([{ all: [{ field: 1, op: 'isEmpty' }] }]);
  });

  it('apply emits pruned condition', async () => {
    const w = mountPanel({ all: [{ field: 1, op: 'eq', value: 'x' }, { field: 2, op: 'eq' }, { field: 0, op: 'eq' }] });
    expect(w.emitted('update:modelValue')).toBeUndefined();
    await w.get('[data-testid="apply"]').trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([{ all: [{ field: 1, op: 'eq', value: 'x' }] }]);
  });

  it('nested condition shows read-only notice', async () => {
    const nested: ConditionNode = { all: [{ field: 1, op: 'eq', value: 'x' }, { any: [{ field: 2, op: 'gt', value: 1 }] }] };
    const w = mountPanel(nested);
    expect(w.text()).toContain('此檢視的篩選較複雜，請在原檢視中使用');
    expect(w.findAllComponents(GenericSelect)).toHaveLength(0);
    await w.get('[data-testid="apply"]').trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([nested]);
  });

  it('autonumber in uses the list input and emits an array', async () => {
    const w = mountPanel({ all: [{ field: 4, op: 'eq', value: 'x' }] });
    w.findAllComponents(GenericSelect)[2]!.vm.$emit('update:modelValue', 'in');
    await w.vm.$nextTick();
    await w.get('textarea[data-testid="value-list"]').setValue('A-1, A-2');
    await w.get('[data-testid="apply"]').trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([{ all: [{ field: 4, op: 'in', value: ['A-1', 'A-2'] }] }]);
  });

  it('text in splits by comma/newline, trims, dedupes and caps at 50 (R7)', async () => {
    const w = mountPanel({ all: [{ field: 1, op: 'eq', value: 'x' }] });
    w.findAllComponents(GenericSelect)[2]!.vm.$emit('update:modelValue', 'in');
    await w.vm.$nextTick();
    const input = w.get('textarea[data-testid="value-list"]');
    await input.setValue(' a, b\nb ,, c ');
    await w.get('[data-testid="apply"]').trigger('click');
    expect(w.emitted('update:modelValue')![0]).toEqual([{ all: [{ field: 1, op: 'in', value: ['a', 'b', 'c'] }] }]);
    await input.setValue(Array.from({ length: 60 }, (_, i) => `v${i}`).join(','));
    await w.get('[data-testid="apply"]').trigger('click');
    const last = w.emitted('update:modelValue')![1]![0] as { all: { value: string[] }[] };
    expect(last.all[0]!.value).toHaveLength(50);
  });

  it('shows a truncation notice for more than 50 text values and sends 50', async () => {
    const w = mountPanel({ all: [{ field: 1, op: 'in', value: ['a'] }] });
    const input = w.get('textarea[data-testid="value-list"]');
    expect(w.find('[data-testid="list-truncated"]').exists()).toBe(false);
    await input.setValue(Array.from({ length: 51 }, (_, i) => `v${i}`).join(','));
    expect(w.get('[data-testid="list-truncated"]').text()).toBe('超過 50 個，只取前 50 個');
    await w.get('[data-testid="apply"]').trigger('click');
    const sent = w.emitted('update:modelValue')![0]![0] as { all: { value: string[] }[] };
    expect(sent.all[0]!.value).toHaveLength(50);
    await input.setValue('a,b');
    expect(w.find('[data-testid="list-truncated"]').exists()).toBe(false);
  });

  it('multienum isEmpty uses the plain operator label', () => {
    const multi: ModuleSchema = {
      ...schema,
      blocks: [{ id: 1, label: { 'zh-TW': '區塊' }, collapsed: false, columns: 2, fields: [field(4, 'multienum', '標籤')] }],
    };
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(FilterPanel, {
      props: { schema: multi, modelValue: { all: [{ field: 4, op: 'isEmpty' }] } },
      global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n] },
    });
    const opSelect = w.findAllComponents(GenericSelect)[2]!;
    const labels = (opSelect.props('options') as { value: string; label: string }[]);
    expect(labels.find((o) => o.value === 'isEmpty')!.label).toBe('是空的');
    expect(labels.find((o) => o.value === 'in')!.label).toBe('含任一');
  });
});
