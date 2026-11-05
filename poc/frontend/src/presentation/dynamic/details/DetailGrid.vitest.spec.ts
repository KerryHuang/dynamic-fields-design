import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { SchemaDetail, SchemaField, SchemaRule } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import type { DetailRowState } from './detail-rows';
import DetailGrid from './DetailGrid.vue';

const field = (id: number, type: SchemaField['type'], label: string): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});
const parent = { ...field(9, 'reference', '表頭') };
const detail = (rules: SchemaRule[] = [], fields: SchemaField[] = [field(1, 'text', '品名'), field(2, 'decimal', '數量')]): SchemaDetail => ({
  moduleKey: 'line', name: { 'zh-TW': '訂單明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_1', fields, rules,
});
const existing = (id: number, values: Record<string, unknown>): DetailRowState => ({
  uid: `u${id}`, id, rowVersion: `v${id}`, values: { ...values }, original: values, deleted: false,
});

function mountGrid(props: { detail?: SchemaDetail; modelValue?: DetailRowState[]; readonly?: boolean; errors?: Map<string, Record<string, string>>; headerValues?: Record<string, unknown>; headerFields?: SchemaField[] }) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(DetailGrid, {
    props: {
      detail: detail(), modelValue: [], readonly: false, errors: new Map(), ...props,
      'onUpdate:modelValue': (v: DetailRowState[]) => w.setProps({ modelValue: v }),
    },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n] },
  });
  return w;
}
const rowsOf = (w: ReturnType<typeof mountGrid>) => w.props('modelValue') as DetailRowState[];

describe('DetailGrid', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('renders one column per detail field, excluding parent field', () => {
    const w = mountGrid({ detail: detail([], [parent, field(1, 'text', '品名'), field(2, 'decimal', '數量')]), modelValue: [existing(1, { f_1: '螺絲', f_2: 3 })] });
    expect(w.findAll('th').map((th) => th.text()).filter(Boolean)).toEqual(['品名', '數量']);
    expect(w.findAll('tbody tr')[0]!.findAll('input')).toHaveLength(2);
  });

  it('computed columns are read-only and show stored values', () => {
    const formula = { ...field(3, 'formula', '金額'), config: { ...field(3, 'formula', '').config, resultType: 'number' as const } };
    const w = mountGrid({
      detail: detail([], [field(1, 'text', '品名'), formula, field(4, 'autonumber', '項次'), field(5, 'rollup', '合計')]),
      modelValue: [existing(1, { f_1: '螺絲', f_3: 30, f_4: '001', f_5: 7 })],
    });
    const inputs = w.findAll('tbody tr')[0]!.findAll('input');
    expect(inputs.map((i) => (i.element as HTMLInputElement).value)).toEqual(['螺絲', '30', '001', '7']);
    expect(inputs.map((i) => i.attributes('readonly') !== undefined)).toEqual([false, true, true, true]);
  });

  it('add row takes the header value for a parent-default field', async () => {
    const dateWithParent = { ...field(3, 'date', '交期'), config: { ...field(3, 'date', '').config, parentDefaultFieldId: 50 } };
    const w = mountGrid({
      detail: detail([], [field(1, 'text', '品名'), dateWithParent]), headerFields: [field(50, 'date', '表頭交期')], headerValues: { f_50: '2026-12-01' },
    });
    await w.find('[data-testid="add-row"]').trigger('click');
    expect(rowsOf(w)[0]!.values).toEqual({ f_1: null, f_3: '2026-12-01' });
  });

  it('add row appends empty row', async () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: '螺絲' })] });
    await w.find('[data-testid="add-row"]').trigger('click');
    const rows = rowsOf(w);
    expect(rows).toHaveLength(2);
    expect(rows[1]!.id).toBeUndefined();
    expect(rows[1]).toMatchObject({ deleted: false, values: { f_1: null, f_2: null } });
    expect(w.findAll('tbody tr')).toHaveLength(2);
    await w.findAll('tbody tr')[1]!.find('input').setValue('螺帽');
    expect(rowsOf(w)[1]!.values.f_1).toBe('螺帽');
    expect(rowsOf(w)[0]!.values.f_1).toBe('螺絲');
  });

  it('delete new row removes it; delete existing row marks deleted and hides it', async () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: '螺絲' })] });
    await w.find('[data-testid="add-row"]').trigger('click');
    await w.findAll('[data-testid="delete-row"]')[1]!.trigger('click');
    expect(rowsOf(w)).toHaveLength(1);
    await w.findAll('[data-testid="delete-row"]')[0]!.trigger('click');
    expect(rowsOf(w)).toHaveLength(1);
    expect(rowsOf(w)[0]!.deleted).toBe(true);
    expect(w.findAll('[data-testid="delete-row"]')).toHaveLength(0);
    expect(w.text()).not.toContain('螺絲');
  });

  it('row rule readOnlyAll locks that row', () => {
    const lock: SchemaRule = { kind: 'readOnly', targets: ['*'], condition: { field: 1, op: 'eq', value: 'closed' }, options: [] };
    const w = mountGrid({ detail: detail([lock]), modelValue: [existing(1, { f_1: 'closed' }), existing(2, { f_1: 'open' })] });
    const [locked, open] = w.findAll('tbody tr');
    expect(locked!.findAll('input').every((i) => i.attributes('readonly') !== undefined)).toBe(true);
    expect(locked!.find('[data-testid="delete-row"]').exists()).toBe(false);
    expect(open!.findAll('input').every((i) => i.attributes('readonly') === undefined)).toBe(true);
    expect(open!.find('[data-testid="delete-row"]').exists()).toBe(true);
  });

  it('row visibility false shows blank non-editable cell', () => {
    const vis: SchemaRule = { kind: 'visibility', targets: ['field:2'], condition: { field: 1, op: 'eq', value: 'show' }, options: [] };
    const w = mountGrid({ detail: detail([vis]), modelValue: [existing(1, { f_1: 'hide', f_2: 5 }), existing(2, { f_1: 'show', f_2: 6 })] });
    const [hidden, shown] = w.findAll('tbody tr');
    expect(hidden!.findAll('input')).toHaveLength(1);
    expect(hidden!.find('[data-testid="cell-hidden"]').exists()).toBe(true);
    expect(hidden!.text()).not.toContain('5');
    expect(shown!.findAll('input')).toHaveLength(2);
  });

  it('cell error shown under input', async () => {
    const w = mountGrid({
      modelValue: [existing(1, { f_1: 'a' }), existing(2, { f_1: 'b' })],
      errors: new Map([['u2', { f_2: '必須是數字', _record: '此記錄目前不可修改' }]]),
    });
    await flushPromises();
    const [first, second] = w.findAll('tbody tr');
    expect(first!.text()).not.toContain('必須是數字');
    const cells = second!.findAll('.q-field');
    expect(cells[1]!.text()).toContain('必須是數字');
    expect(cells[0]!.text()).not.toContain('必須是數字');
    expect(second!.find('[data-testid="row-error"]').text()).toContain('此記錄目前不可修改');
  });

  it('rule-required cell is marked required', () => {
    const req: SchemaRule = { kind: 'required', targets: ['field:2'], condition: { field: 1, op: 'eq', value: 'go' }, options: [] };
    const w = mountGrid({ detail: detail([req]), modelValue: [existing(1, { f_1: 'go' }), existing(2, { f_1: 'stay' })] });
    const qty = w.findAllComponents({ name: 'MoneyNumberInput' });
    expect(qty.map((c) => c.props('required'))).toEqual([true, false]);
    expect(w.findAll('th').map((th) => th.text())).not.toContain('數量 *');
  });
});
