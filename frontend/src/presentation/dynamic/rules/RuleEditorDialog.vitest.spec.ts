import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { Rule, SchemaField } from 'src/domain/dynamic/types';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi18n from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';
import RuleEditorDialog from './RuleEditorDialog.vue';

const field = (id: number, type: SchemaField['type'], label: string, options: SchemaField['options'] = []): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options,
  config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false },
});
const opt = (id: number, label: string, active = true) => ({ value: `opt_${id}`, label: { 'zh-TW': label }, active });
const fields = [
  field(1, 'integer', '數量'), field(2, 'enum', '狀態', [opt(10, '草稿'), opt(11, '結案')]),
  field(3, 'enum', '類別', [opt(20, '甲')]), field(4, 'formula', '合計'),
];
const blocks = [{ id: 7, label: { 'zh-TW': '基本資料' } }];

function mountDialog(rule?: Rule) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(RuleEditorDialog, {
    props: { modelValue: true, fields, blocks, rule },
    attachTo: document.body,
    // QDialog teleports its content out of the wrapper's tree; render it inline so component finders reach it.
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n], stubs: { QDialog: { template: '<div><slot /></div>' } } },
  });
}
type W = ReturnType<typeof mountDialog>;
const sel = (w: W, id: string) => w.findAllComponents(GenericSelect).find((c) => c.vm.$attrs['data-testid'] === id)!;
const targetValues = (w: W) => (sel(w, 'rule-targets').props('options') as { value: string }[]).map((o) => o.value);
const click = (id: string) => (document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement).click();

describe('RuleEditorDialog', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('visibility targets fields and blocks, readOnly adds the whole record, required is fields only', async () => {
    const w = mountDialog();
    await flushPromises();
    sel(w, 'rule-kind').vm.$emit('update:modelValue', 'visibility');
    await flushPromises();
    expect(targetValues(w)).toEqual(['field:1', 'field:2', 'field:3', 'field:4', 'block:7']);
    sel(w, 'rule-kind').vm.$emit('update:modelValue', 'readOnly');
    await flushPromises();
    expect(targetValues(w)).toEqual(['field:1', 'field:2', 'field:3', 'field:4', 'block:7', '*']);
    sel(w, 'rule-kind').vm.$emit('update:modelValue', 'required');
    await flushPromises();
    expect(targetValues(w)).toEqual(['field:1', 'field:2', 'field:3', 'field:4']);
    w.unmount();
  });

  it('optionFilter picks one dropdown field, then its options; needs at least one option', async () => {
    const w = mountDialog();
    await flushPromises();
    sel(w, 'rule-kind').vm.$emit('update:modelValue', 'optionFilter');
    await flushPromises();
    expect((sel(w, 'rule-option-target').props('options') as { value: string }[]).map((o) => o.value)).toEqual(['field:2', 'field:3']);
    sel(w, 'rule-option-target').vm.$emit('update:modelValue', 'field:2');
    await flushPromises();
    click('rule-save');
    await flushPromises();
    expect(w.emitted('save')).toBeUndefined();
    expect(document.body.querySelector('[data-testid="rule-error"]')?.textContent).toBe(zhTW.designer.rules.optionRequired);
    const boxes = w.findAllComponents(quasar.QCheckbox).filter((c) => String(c.vm.$attrs['data-testid'] ?? '').startsWith('rule-opt-'));
    expect(boxes).toHaveLength(2);
    boxes[1]!.vm.$emit('update:modelValue', true);
    await flushPromises();
    click('rule-save');
    await flushPromises();
    expect(w.emitted('save')![0]).toEqual([{
      kind: 'optionFilter', condition: { all: [] }, targets: ['field:2'], optionIds: [11], active: true,
    }]);
    w.unmount();
  });

  it('validation shows the message input and sends the message with the rule', async () => {
    const w = mountDialog();
    await flushPromises();
    expect(document.body.querySelector('[data-testid="rule-message"]')).toBeNull();
    sel(w, 'rule-kind').vm.$emit('update:modelValue', 'validation');
    await flushPromises();
    expect(document.body.querySelector('[data-testid="rule-message"]')).not.toBeNull();
    sel(w, 'rule-targets').vm.$emit('update:modelValue', ['field:1']);
    const zh = document.body.querySelector('[data-testid="i18n-zh-TW"] input') as HTMLInputElement;
    zh.value = '數量不可為負';
    zh.dispatchEvent(new Event('input'));
    await flushPromises();
    click('rule-save');
    await flushPromises();
    expect(w.emitted('save')![0]).toEqual([{
      kind: 'validation', condition: { all: [] }, targets: ['field:1'], optionIds: [], message: { 'zh-TW': '數量不可為負' }, active: true,
    }]);
    w.unmount();
  });

  it('requires a target', async () => {
    const w = mountDialog();
    await flushPromises();
    click('rule-save');
    await flushPromises();
    expect(w.emitted('save')).toBeUndefined();
    expect(document.body.querySelector('[data-testid="rule-error"]')?.textContent).toBe(zhTW.designer.rules.targetRequired);
    w.unmount();
  });

  it('edit keeps id and active; hint appears when the condition uses a formula field', async () => {
    const w = mountDialog({
      id: 5, kind: 'required', condition: { all: [{ field: 4, op: 'gt', value: 1 }] }, targets: ['field:1'], optionIds: [], active: false,
    });
    await flushPromises();
    expect(document.body.textContent).toContain('條件含公式或自動編號欄位：表單上不會即時套用，儲存時由伺服器判斷');
    click('rule-save');
    await flushPromises();
    expect(w.emitted('save')![0]).toEqual([{
      id: 5, kind: 'required', condition: { all: [{ field: 4, op: 'gt', value: 1 }] }, targets: ['field:1'], optionIds: [], active: false,
    }]);
    w.unmount();
  });

  it('has the computed-field hint in every locale', () => {
    for (const m of [zhTW, enUS, th, vi18n]) expect(m.designer.rules.computedHint).toBeTruthy();
  });
});
