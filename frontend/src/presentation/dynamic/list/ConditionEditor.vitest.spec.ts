import { mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { ConditionNode, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import ConditionEditor from './ConditionEditor.vue';

const field = (id: number, type: SchemaField['type'], label: string): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false },
});
const fields = [field(1, 'integer', '數量'), field(2, 'decimal', '上限'), field(3, 'text', '名稱'), field(4, 'textarea', '備註')];

function mountEditor(props: { modelValue?: ConditionNode; allowFieldRef?: boolean }) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(ConditionEditor, {
    props: { fields, ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n] },
  });
}
const last = (w: ReturnType<typeof mountEditor>) => w.emitted('update:modelValue')!.at(-1)![0];

describe('ConditionEditor', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('lets a condition row wrap on narrow screens instead of forcing no-wrap', () => {
    const w = mountEditor({ modelValue: { all: [{ field: 1, op: 'lte', value: 5 }] } });
    const row = w.find('.condition-row');
    expect(row.exists()).toBe(true);
    expect(row.classes()).not.toContain('no-wrap');
  });

  it('offers no field reference unless allowFieldRef', () => {
    const w = mountEditor({ modelValue: { all: [{ field: 1, op: 'lte', value: 5 }] } });
    expect(w.findAllComponents(GenericSelect)).toHaveLength(3);
  });

  it('switches the comparison to another field, listing only fields of the same index kind', async () => {
    const w = mountEditor({ modelValue: { all: [{ field: 1, op: 'lte', value: 5 }] }, allowFieldRef: true });
    const sel = () => w.findAllComponents(GenericSelect);
    sel()[3]!.vm.$emit('update:modelValue', 'field');
    await w.vm.$nextTick();
    const refSelect = sel()[4]!;
    expect((refSelect.props('options') as { label: string }[]).map((o) => o.label)).toEqual(['上限']);
    refSelect.vm.$emit('update:modelValue', '2');
    await w.vm.$nextTick();
    expect(last(w)).toEqual({ all: [{ field: 1, op: 'lte', value: { fieldRef: 2 } }] });
  });

  it('hides the field reference for operators that cannot take one', () => {
    const w = mountEditor({ modelValue: { all: [{ field: 1, op: 'isEmpty' }] }, allowFieldRef: true });
    expect(w.findAllComponents(GenericSelect)).toHaveLength(3);
  });

  it('rule mode offers isEmpty/isNotEmpty on textarea', () => {
    const w = mountEditor({ modelValue: { all: [{ field: 4, op: 'isEmpty' }] }, allowFieldRef: true });
    const ops = w.findAllComponents(GenericSelect)[2]!.props('options') as { value: string }[];
    expect(ops.map((o) => o.value)).toEqual(['isEmpty', 'isNotEmpty']);
  });

  it('falls back to JSON editing for nested conditions', async () => {
    const nested: ConditionNode = { all: [{ field: 1, op: 'eq', value: 1 }, { any: [{ field: 2, op: 'gt', value: 1 }] }] };
    const w = mountEditor({ modelValue: nested, allowFieldRef: true });
    expect(w.findAllComponents(GenericSelect)).toHaveLength(0);
    const ta = w.get('textarea[data-testid="condition-json"]');
    expect((ta.element as HTMLTextAreaElement).value).toContain('"any"');
    await ta.setValue('{"any":[{"field":3,"op":"eq","value":"x"}]}');
    expect(last(w)).toEqual({ any: [{ field: 3, op: 'eq', value: 'x' }] });
  });

  it('reloads rows when the parent clears the model and later sets the same condition again', async () => {
    const w = mountEditor({});
    w.findAll('button').find((b) => b.text().includes('新增條件'))!.trigger('click');
    await w.vm.$nextTick();
    w.findAllComponents(GenericSelect)[1]!.vm.$emit('update:modelValue', '3');
    await w.vm.$nextTick();
    const built = last(w);
    await w.setProps({ modelValue: built as ConditionNode });
    await w.setProps({ modelValue: undefined });
    expect(w.findAllComponents(GenericSelect)).toHaveLength(1);
    await w.setProps({ modelValue: built as ConditionNode });
    expect(w.findAllComponents(GenericSelect)).toHaveLength(3);
  });
});
