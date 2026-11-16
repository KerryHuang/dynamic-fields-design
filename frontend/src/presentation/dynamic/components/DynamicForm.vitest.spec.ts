import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DynamicRecord, ModuleSchema, SchemaDetail, SchemaField, SchemaRule } from 'src/domain/dynamic/types';
import DetailGrid from 'src/presentation/dynamic/details/DetailGrid.vue';
import zhTW from 'src/i18n/zh-TW.json';
import DynamicForm from './DynamicForm.vue';

const field = (id: number, type: SchemaField['type'], label: string, required = false): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});

const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [
    { id: 1, label: { 'zh-TW': '基本資料' }, collapsed: false, columns: 2, fields: [field(1, 'text', '名稱', true), field(2, 'decimal', '金額')] },
    { id: 2, label: { 'zh-TW': '備註區' }, collapsed: true, columns: 2, fields: [field(3, 'text', '備註')] },
  ],
};

const record: DynamicRecord = {
  id: 7, moduleKey: 'm', title: 'abc', values: { f_1: 'abc', f_2: 10.5, f_3: ' note ' }, ownerUserId: 'admin',
  createdUserId: 'admin', createdTime: '', modifiedUserId: 'admin', modifiedTime: '', rowVersion: 'rv1', references: {}, hostKey: null,
};

function mountForm(props: Record<string, unknown>, locale = 'zh-TW') {
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW } });
  return mount(DynamicForm, {
    props: { schema, mode: 'create', ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n] },
  });
}

describe('DynamicForm', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('shows backend field errors under the right field', async () => {
    const w = mountForm({});
    await w.setProps({ serverErrors: { f_1: ['此欄位必填'] } });
    await flushPromises();
    const fields = w.findAll('.q-field');
    expect(fields[0]!.text()).toContain('此欄位必填');
    expect(fields[1]!.text()).not.toContain('此欄位必填');
  });

  it('collapsed block starts collapsed', () => {
    const w = mountForm({});
    const items = w.findAll('.q-expansion-item');
    expect(items).toHaveLength(2);
    expect(items[0]!.classes()).not.toContain('q-expansion-item--collapsed');
    expect(items[1]!.classes()).toContain('q-expansion-item--collapsed');
  });

  it('lays fields out by the block columns; textarea takes the whole row', () => {
    const s: ModuleSchema = { ...schema, blocks: [{ id: 1, label: { 'zh-TW': 'b' }, collapsed: false, columns: 3,
      fields: [field(1, 'text', '名稱'), field(2, 'textarea', '說明'), field(3, 'decimal', '金額')] }] };
    const w = mountForm({ schema: s });
    const cells = w.findAll('.row.q-col-gutter-md > div');
    expect(cells.map((c) => c.classes().filter((x) => x.startsWith('col')).join(' '))).toEqual([
      'col-12 col-sm-6 col-md-4', 'col-12', 'col-12 col-sm-6 col-md-4',
    ]);
  });

  it('submits only changed values in edit mode', async () => {
    const w = mountForm({ mode: 'edit', record });
    await flushPromises();
    await w.findAll('input')[0]!.setValue('xyz');
    await w.find('form').trigger('submit');
    await vi.waitFor(() => expect(w.emitted('submit')).toBeTruthy());
    expect(w.emitted('submit')).toEqual([[{ f_1: 'xyz' }, {}]]); // untouched f_3 (' note ', which zod would trim) is not sent
  });

  it('submits nothing extra when nothing was touched in edit mode', async () => {
    const w = mountForm({ mode: 'edit', record });
    await flushPromises();
    await w.find('form').trigger('submit');
    await vi.waitFor(() => expect(w.emitted('submit')).toBeTruthy());
    expect(w.emitted('submit')).toEqual([[{}, {}]]);
  });

  it('create mode drops inactive options from enum/multienum defaults (R19)', async () => {
    const opt = (id: number, active: boolean) => ({ value: `opt_${id}`, label: { 'zh-TW': `o${id}` }, active });
    const en: SchemaField = {
      ...field(4, 'enum', '狀態'), options: [opt(1, false), opt(2, true)], config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, defaultValue: 'opt_1' },
    };
    const multi: SchemaField = {
      ...field(5, 'multienum', '標籤'), options: [opt(1, false), opt(2, true)], config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, defaultValue: ['opt_1', 'opt_2'] },
    };
    const s: ModuleSchema = { ...schema, blocks: [{ id: 1, label: { 'zh-TW': 'b' }, collapsed: false, columns: 2, fields: [field(1, 'text', '名稱', true), en, multi] }] };
    const w = mountForm({ schema: s });
    await flushPromises();
    await w.findAll('input')[0]!.setValue('abc');
    await w.find('form').trigger('submit');
    await vi.waitFor(() => expect(w.emitted('submit')).toBeTruthy());
    const sent = (w.emitted('submit')![0] as [Record<string, unknown>])[0];
    expect(sent.f_4 ?? null).toBeNull();
    expect(sent.f_5).toEqual(['opt_2']);
  });

  describe('frozen contributed fields (shared master)', () => {
    const lock = { appKey: 'crm', appName: { 'zh-TW': '客戶關係' } };
    const locked: SchemaField = { ...field(3, 'text', '備註'), lockedBy: lock, config: { ...field(3, 'text', '備註').config, defaultValue: '預設' } };
    const s: ModuleSchema = { ...schema, blocks: [schema.blocks[0]!, { id: 2, label: { 'zh-TW': 'CRM' }, collapsed: true, columns: 2, fields: [locked] }] };

    it('shows the field read-only with the closed app named in its block', async () => {
      const w = mountForm({ schema: s, mode: 'edit', record });
      await flushPromises();
      expect(w.find('[data-testid="app-closed-lock"]').text()).toContain('「客戶關係」應用程式已關閉');
      const dyn = w.findAllComponents({ name: 'DynamicField' }).find((c) => (c.props('field') as SchemaField).id === 3)!;
      expect(dyn.props('readonly')).toBe(true);
    });

    it('a lock given only on the record also makes the field read-only', async () => {
      const w = mountForm({ mode: 'edit', record: { ...record, lockedBy: { f_3: lock } } });
      await flushPromises();
      const dyn = w.findAllComponents({ name: 'DynamicField' }).find((c) => (c.props('field') as SchemaField).id === 3)!;
      expect(dyn.props('readonly')).toBe(true);
      expect(w.findAll('[data-testid="app-closed-lock"]')).toHaveLength(1);
    });

    it('create mode does not send a default for a frozen field (the server would 409)', async () => {
      const w = mountForm({ schema: s });
      await flushPromises();
      await w.findAll('input')[0]!.setValue('abc');
      await w.find('form').trigger('submit');
      await vi.waitFor(() => expect(w.emitted('submit')).toBeTruthy());
      const sent = (w.emitted('submit')![0] as [Record<string, unknown>])[0];
      expect(sent.f_3 ?? null).toBeNull();
    });
  });

  describe('rules', () => {
    const opt = (id: number) => ({ value: `opt_${id}`, label: { 'zh-TW': `o${id}` }, active: true });
    const rule = (r: Partial<SchemaRule> & Pick<SchemaRule, 'kind' | 'targets'>): SchemaRule => ({
      condition: { field: 1, op: 'eq', value: 'go' }, options: [], ...r,
    });
    const rulesSchema = (rules: SchemaRule[], extra: SchemaField[] = [field(2, 'text', '甲')]): ModuleSchema => ({
      ...schema, rules,
      blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, 'text', '名稱'), ...extra] }],
    });
    const submit = async (w: ReturnType<typeof mountForm>) => {
      await w.find('form').trigger('submit');
      await new Promise((r) => setTimeout(r, 30)); // vee-validate validates asynchronously
      await flushPromises();
    };

    it('visibility target is shown only while the condition holds', async () => {
      const w = mountForm({ schema: rulesSchema([rule({ kind: 'visibility', targets: ['field:2'] })]) });
      await flushPromises();
      expect(w.findAll('input')).toHaveLength(1);
      await w.findAll('input')[0]!.setValue('go');
      await flushPromises();
      expect(w.findAll('input')).toHaveLength(2);
    });

    it('hides a block whose fields are all hidden', async () => {
      const s = rulesSchema([rule({ kind: 'visibility', targets: ['block:2'] })]);
      s.blocks.push({ id: 2, label: { 'zh-TW': '第二區' }, collapsed: false, columns: 2, fields: [field(3, 'text', '丙')] });
      const w = mountForm({ schema: s });
      await flushPromises();
      expect(w.text()).not.toContain('第二區');
    });

    it('a hidden required field does not block submit', async () => {
      const s = rulesSchema([rule({ kind: 'visibility', targets: ['field:2'] })], [field(2, 'text', '甲', true)]);
      const w = mountForm({ schema: s });
      await flushPromises();
      await submit(w);
      expect(w.emitted('submit')).toBeTruthy();
    });

    it('a Required rule marks the field and rejects an empty value on submit', async () => {
      const w = mountForm({ schema: rulesSchema([rule({ kind: 'required', targets: ['field:2'] })]) });
      await flushPromises();
      expect(w.text()).not.toContain('甲 *');
      await w.findAll('input')[0]!.setValue('go');
      await flushPromises();
      expect(w.text()).toContain('甲 *');
      await submit(w);
      expect(w.emitted('submit')).toBeFalsy();
      expect(w.text()).toContain('此欄位必填');
    });

    it('a failed Validation blocks submit and shows the message of the current locale on the target', async () => {
      const r = rule({
        kind: 'validation', targets: ['field:2'], condition: { field: 1, op: 'ne', value: 'bad' },
        message: { 'zh-TW': '名稱不可為 bad', 'en-US': 'Name must not be bad' },
      });
      const w = mountForm({ schema: rulesSchema([r]) }, 'en-US');
      await flushPromises();
      await w.findAll('input')[0]!.setValue('bad');
      await submit(w);
      expect(w.emitted('submit')).toBeFalsy();
      expect(w.findAll('.q-field')[1]!.text()).toContain('Name must not be bad');
    });

    it('a Validation whose targets are all hidden reports on the top of the form (M3)', async () => {
      const r = rule({
        kind: 'validation', targets: ['field:2'], condition: { field: 1, op: 'ne', value: 'bad' }, message: { 'zh-TW': '不可為 bad' },
      });
      const hide = rule({ kind: 'visibility', targets: ['field:2'], condition: { field: 1, op: 'eq', value: 'never' } });
      const w = mountForm({ schema: rulesSchema([hide, r]) });
      await flushPromises();
      await w.findAll('input')[0]!.setValue('bad');
      await submit(w);
      expect(w.emitted('submit')).toBeFalsy();
      expect(w.find('[data-testid="record-error"]').text()).toContain('不可為 bad');
    });

    describe('OptionFilter', () => {
      const child = (): SchemaField => ({
        ...field(2, 'enum', '子'), options: [opt(1), opt(2)],
        config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, defaultValue: 'opt_2' },
      });
      const filter = rule({ kind: 'optionFilter', targets: ['field:2'], options: ['opt_1'] });

      it('only the allowed options are offered once the filter holds', async () => {
        const w = mountForm({ schema: rulesSchema([filter], [child()]) });
        await flushPromises();
        const optionsOf = () => (w.findComponent({ name: 'GenericSelect' }).props('options') as { value: string }[]).map((o) => o.value);
        expect(optionsOf()).toEqual(['opt_1', 'opt_2']);
        await w.findAll('input')[0]!.setValue('go');
        await flushPromises();
        expect(optionsOf()).toEqual(['opt_1']);
      });

      it('an old child value out of range blocks submit with the verbatim message', async () => {
        const w = mountForm({ schema: rulesSchema([filter], [child()]) });
        await flushPromises();
        await w.findAll('input')[0]!.setValue('go');
        await submit(w);
        expect(w.emitted('submit')).toBeFalsy();
        expect(w.text()).toContain('選項不在可選範圍');
      });
    });

    describe('ReadOnly', () => {
      const lock = rule({ kind: 'readOnly', targets: ['*'], condition: { field: 1, op: 'eq', value: 'closed' } });
      const rec = (v: string): DynamicRecord => ({ ...record, values: { f_1: v, f_2: 'x' } });

      it('a stored record that satisfies the condition makes the whole form read-only without a save button', async () => {
        const w = mountForm({ schema: rulesSchema([lock]), mode: 'edit', record: rec('closed') });
        await flushPromises();
        expect(w.find('button[type="submit"]').exists()).toBe(false);
        expect(w.text()).toContain(zhTW.dynamic.recordReadOnly);
        expect(w.findAll('input').every((i) => i.attributes('readonly') !== undefined)).toBe(true);
      });

      it('editing the value to satisfy the condition does not lock the form yet (evaluated on stored values)', async () => {
        const w = mountForm({ schema: rulesSchema([lock]), mode: 'edit', record: rec('open') });
        await flushPromises();
        await w.findAll('input')[0]!.setValue('closed');
        await flushPromises();
        expect(w.find('button[type="submit"]').exists()).toBe(true);
        expect(w.text()).not.toContain(zhTW.dynamic.recordReadOnly);
        await submit(w);
        expect(w.emitted('submit')).toEqual([[{ f_1: 'closed' }, {}]]);
      });
    });

    it('shows a server _record error at the top of the form', async () => {
      const w = mountForm({});
      await w.setProps({ serverErrors: { _record: ['此記錄目前不可修改'] } });
      await flushPromises();
      await vi.waitFor(() => expect(w.find('[data-testid="record-error"]').text()).toContain('此記錄目前不可修改'));
    });

    it('a hidden field whose value changed is still submitted (decision 8)', async () => {
      const s = rulesSchema([rule({ kind: 'visibility', targets: ['field:2'], condition: { field: 1, op: 'ne', value: 'hideme' } })]);
      const w = mountForm({ schema: s, mode: 'edit', record: { ...record, values: { f_1: 'a', f_2: 'x' } } });
      await flushPromises();
      await w.findAll('input')[1]!.setValue('y');
      await w.findAll('input')[0]!.setValue('hideme');
      await flushPromises();
      expect(w.findAll('input')).toHaveLength(1);
      await submit(w);
      expect(w.emitted('submit')).toEqual([[{ f_1: 'hideme', f_2: 'y' }, {}]]);
    });

    it('does not evaluate rules that reference a formula field on the client (server judges them on save)', async () => {
      const total = { ...field(3, 'decimal', '合計'), type: 'formula' as const };
      const r = rule({ kind: 'validation', targets: ['field:2'], condition: { field: 3, op: 'lte', value: 1000 }, message: { 'zh-TW': '合計過大' } });
      const w = mountForm({ schema: rulesSchema([r], [field(2, 'text', '甲'), total]) });
      await flushPromises();
      await submit(w);
      expect(w.emitted('submit')).toBeTruthy();
    });

    describe('errors on fields that are not rendered', () => {
      const hideB = rule({ kind: 'visibility', targets: ['field:2'], condition: { field: 1, op: 'ne', value: 'hideme' } });
      const hiddenSchema = () => rulesSchema([hideB], [field(2, 'integer', '甲')]);

      it('shows a client error of a hidden field in the top banner as label：message', async () => {
        const w = mountForm({ schema: hiddenSchema(), mode: 'edit', record: { ...record, values: { f_1: 'a', f_2: 3 } } });
        await flushPromises();
        await w.findAll('input')[1]!.setValue('1.5');
        await w.findAll('input')[0]!.setValue('hideme');
        await flushPromises();
        await submit(w);
        expect(w.emitted('submit')).toBeFalsy();
        expect(w.find('[data-testid="record-error"]').text()).toContain('甲：必須是整數');
      });

      it('shows a server error keyed to a hidden field in the top banner', async () => {
        const w = mountForm({ schema: hiddenSchema() });
        await flushPromises();
        await w.findAll('input')[0]!.setValue('hideme');
        // vee-validate debounces schema validation with a real 5 ms timer that flushPromises does not drain.
        await new Promise((r) => setTimeout(r, 30));
        await flushPromises();
        await w.setProps({ serverErrors: { f_2: ['伺服器拒絕'] } });
        await vi.waitFor(() => expect(w.find('[data-testid="record-error"]').text()).toContain('甲：伺服器拒絕'));
      });
    });

    it('does not submit a read-only form (Enter in an input)', async () => {
      const lock = rule({ kind: 'readOnly', targets: ['*'], condition: { field: 1, op: 'eq', value: 'closed' } });
      const w = mountForm({ schema: rulesSchema([lock]), mode: 'edit', record: { ...record, values: { f_1: 'closed', f_2: 'x' } } });
      await flushPromises();
      await submit(w);
      expect(w.emitted('submit')).toBeFalsy();
    });
  });

  describe('details', () => {
    const line: SchemaDetail = {
      moduleKey: 'line', name: { 'zh-TW': '訂單明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_11',
      fields: [field(11, 'text', '品名'), field(12, 'decimal', '數量')], rules: [],
    };
    const part: SchemaDetail = { ...line, moduleKey: 'part', name: { 'zh-TW': '零件' }, fields: [field(21, 'text', '零件')] };
    const withDetails = (rules: SchemaRule[] = []): ModuleSchema => ({ ...schema, rules, details: [line, part] });
    const row = (id: number, values: Record<string, unknown>): DynamicRecord => ({ ...record, id, moduleKey: 'line', values, rowVersion: `v${id}` });
    const header: DynamicRecord = { ...record, details: { line: [row(31, { f_11: '螺絲', f_12: 3 }), row(32, { f_11: '螺帽', f_12: 1 })], part: [] } };
    const submit = async (w: ReturnType<typeof mountForm>) => {
      await w.find('form').trigger('submit');
      await new Promise((r) => setTimeout(r, 30));
      await flushPromises();
    };

    it('renders DetailGrid per schema.details below blocks', async () => {
      const w = mountForm({ schema: withDetails(), mode: 'edit', record: header });
      await flushPromises();
      const grids = w.findAllComponents(DetailGrid);
      expect(grids.map((g) => g.props('detail').moduleKey)).toEqual(['line', 'part']);
      expect(grids[0]!.props('modelValue')).toHaveLength(2);
      const html = w.html();
      expect(html.indexOf('備註區')).toBeLessThan(html.indexOf('訂單明細'));
    });

    it('submit emits details payload', async () => {
      const w = mountForm({ schema: withDetails(), mode: 'edit', record: header });
      await flushPromises();
      const grid = w.findAllComponents(DetailGrid)[0]!;
      await grid.findAll('tbody tr')[1]!.findAll('input')[1]!.setValue('2');
      await grid.find('[data-testid="add-row"]').trigger('click');
      await grid.findAll('tbody tr')[2]!.find('input').setValue('墊片');
      await grid.findAll('[data-testid="delete-row"]')[0]!.trigger('click');
      await submit(w);
      const [values, details] = w.emitted('submit')![0] as [Record<string, unknown>, Record<string, unknown>];
      expect(values).toEqual({});
      expect(details).toEqual({
        line: [
          { id: 31, rowVersion: 'v31', _delete: true },
          { id: 32, rowVersion: 'v32', values: { f_12: 2 } },
          { values: { f_11: '墊片', f_12: null } },
        ],
      });
      expect((w.vm as unknown as { uidAt: Record<string, string[]> }).uidAt.line).toHaveLength(3);
    });

    it('client row validation blocks submit', async () => {
      const required = { ...line, fields: [{ ...field(11, 'text', '品名', true) }, field(12, 'decimal', '數量')] };
      const w = mountForm({ schema: { ...schema, details: [required] }, mode: 'edit', record: header });
      await flushPromises();
      const grid = w.findComponent(DetailGrid);
      await grid.find('[data-testid="add-row"]').trigger('click');
      await submit(w);
      expect(w.emitted('submit')).toBeFalsy();
      expect(grid.findAll('tbody tr')[2]!.text()).toContain('此欄位必填');
    });

    it('readOnlyAll header makes grids read-only', async () => {
      const lock: SchemaRule = { kind: 'readOnly', targets: ['*'], condition: { field: 1, op: 'eq', value: 'closed' }, options: [] };
      const w = mountForm({ schema: withDetails([lock]), mode: 'edit', record: { ...header, values: { f_1: 'closed' } } });
      await flushPromises();
      const grid = w.findAllComponents(DetailGrid)[0]!;
      expect(grid.props('readonly')).toBe(true);
      expect(grid.find('[data-testid="add-row"]').exists()).toBe(false);
      expect(grid.findAll('input').every((i) => i.attributes('readonly') !== undefined)).toBe(true);
    });

    it('server detail errors land on the submitted row', async () => {
      const w = mountForm({ schema: withDetails(), mode: 'edit', record: header });
      await flushPromises();
      const grid = w.findAllComponents(DetailGrid)[0]!;
      await grid.findAll('tbody tr')[1]!.findAll('input')[0]!.setValue('改');
      await submit(w);
      await w.setProps({ detailErrors: { 'details.line[0].f_11': ['伺服器拒絕'] } });
      await flushPromises();
      expect(grid.findAll('tbody tr')[1]!.text()).toContain('伺服器拒絕');
      expect(grid.findAll('tbody tr')[0]!.text()).not.toContain('伺服器拒絕');
    });

    it('editing a cell clears its server error', async () => {
      const w = mountForm({ schema: withDetails(), mode: 'edit', record: header });
      await flushPromises();
      const grid = w.findAllComponents(DetailGrid)[0]!;
      await grid.findAll('tbody tr')[1]!.findAll('input')[0]!.setValue('改');
      await submit(w);
      await w.setProps({ detailErrors: { 'details.line[0].f_11': ['伺服器拒絕'], 'details.line[0].f_12': ['數量不對'], 'details.line[0]._record': ['此記錄目前不可修改'] } });
      await flushPromises();
      await grid.findAll('tbody tr')[1]!.findAll('input')[0]!.setValue('再改');
      await flushPromises();
      const text = grid.findAll('tbody tr')[1]!.text();
      expect(text).not.toContain('伺服器拒絕');
      expect(text).toContain('數量不對');
      expect(text).toContain('此記錄目前不可修改');
    });

    it('server _record error survives client cell error on same row', async () => {
      const w = mountForm({ schema: withDetails(), mode: 'edit', record: header });
      await flushPromises();
      const grid = w.findAllComponents(DetailGrid)[0]!;
      await grid.findAll('tbody tr')[1]!.findAll('input')[0]!.setValue('改');
      await submit(w);
      await w.setProps({ detailErrors: { 'details.line[0]._record': ['此記錄目前不可修改'] } });
      await flushPromises();
      // An invalid number on the same row gives it a client error; the server row error must stay.
      const qty = grid.findAll('tbody tr')[1]!.findAllComponents({ name: 'MoneyNumberInput' })[0]!;
      qty.vm.$emit('update:modelValue', 'abc');
      await flushPromises();
      const row = grid.findAll('tbody tr')[1]!;
      expect(row.text()).toContain('必須是數字');
      expect(row.find('[data-testid="row-error"]').text()).toContain('此記錄目前不可修改');
    });
  });
});
