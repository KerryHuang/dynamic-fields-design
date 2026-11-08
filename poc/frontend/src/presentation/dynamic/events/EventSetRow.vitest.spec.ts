import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { emptySet, type SetDraft } from 'src/domain/dynamic/event-draft';
import type { DesignerField, FieldType } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { findInputByTestId } from 'src/test-utils/find-by-testid';
import EventSetRow from './EventSetRow.vue';

const field = (id: number, type: FieldType, options: DesignerField['options'] = []): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status: 'Active', options,
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});
const opt = (id: number) => ({ id, value: `opt_${id}`, label: { 'zh-TW': `選項${id}` }, sequence: id, active: true });

function mountRow(row: SetDraft, extra: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(EventSetRow, {
    props: {
      modelValue: row, idPrefix: 'set-0-0', kind: 'create', toOptions: [{ value: 71, label: '欄71' }], targetField: undefined,
      headerOptions: [{ value: 22, label: '欄22' }, { value: 23, label: '欄23' }], lineOptions: [{ value: 41, label: '欄41' }], hasLine: true,
      hiddenHeader: new Set<number>(), hiddenLine: new Set<number>(), ...extra,
    },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] },
  });
}
const comp = (w: ReturnType<typeof mountRow>, id: string) => findInputByTestId(w, id);
const lastEmit = (w: ReturnType<typeof mountRow>) => w.emitted('update:modelValue')!.at(-1)![0] as SetDraft;

describe('EventSetRow', () => {
  beforeEach(() => vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }));

  it('picking the target field and a source field emits the row', async () => {
    const w = mountRow({ ...emptySet() });
    comp(w, 'set-0-0-to')!.vm.$emit('update:modelValue', 71);
    expect(lastEmit(w)).toMatchObject({ to: 71 });
    comp(w, 'set-0-0-field')!.vm.$emit('update:modelValue', 22);
    expect(lastEmit(w)).toMatchObject({ source: 'from', fieldId: 22 });
  });

  it('changing the value source clears what belonged to the old one', async () => {
    const w = mountRow({ ...emptySet(), to: 71, source: 'from', fieldId: 22, allowHiddenSource: true });
    comp(w, 'set-0-0-source')!.vm.$emit('update:modelValue', 'formula');
    expect(lastEmit(w)).toMatchObject({ source: 'formula', fieldId: null, expr: '', allowHiddenSource: false });
  });

  it('reading the detail row switches the field list', async () => {
    const w = mountRow({ ...emptySet(), to: 71, source: 'from', side: 'line', fieldId: 41 });
    expect((comp(w, 'set-0-0-field')!.props('options') as { value: number }[]).map((o) => o.value)).toEqual([41]);
  });

  it('an update effect has no detail side to choose', () => {
    const w = mountRow({ ...emptySet() }, { kind: 'update', hasLine: false });
    expect(comp(w, 'set-0-0-side')).toBeUndefined();
  });

  it('asks for consent only when the source is hidden, with the standard explanation', async () => {
    const hidden = new Set([22]);
    const w = mountRow({ ...emptySet(), to: 71, source: 'from', fieldId: 22 }, { hiddenHeader: hidden });
    expect(w.text()).toContain('允許複製被隱藏的值');
    expect(w.text()).toContain('會把被隱藏的值複製到目標模組，看得到目標的人就看得到。');
    comp(w, 'set-0-0-allow-hidden')!.vm.$emit('update:modelValue', true);
    expect(lastEmit(w)).toMatchObject({ allowHiddenSource: true });
    const visible = mountRow({ ...emptySet(), to: 71, source: 'from', fieldId: 23 }, { hiddenHeader: hidden });
    expect(visible.text()).not.toContain('允許複製被隱藏的值');
  });

  it('a hidden field inside a formula needs consent too, and changing the field withdraws it', async () => {
    const w = mountRow({ ...emptySet(), to: 71, source: 'formula', expr: '-{22}', allowHiddenSource: true }, { hiddenHeader: new Set([22]) });
    expect(comp(w, 'set-0-0-allow-hidden')).toBeDefined();
    comp(w, 'set-0-0-expr')!.vm.$emit('update:modelValue', '-{23}');
    expect(lastEmit(w)).toMatchObject({ expr: '-{23}', allowHiddenSource: false });
  });

  it('inserts a field reference into the formula', async () => {
    const w = mountRow({ ...emptySet(), to: 71, source: 'formula', expr: '-' });
    comp(w, 'set-0-0-insert')!.vm.$emit('update:modelValue', 23);
    expect(lastEmit(w)).toMatchObject({ expr: '-{23}' });
  });

  describe('constant', () => {
    const constRow = (): SetDraft => ({ ...emptySet(), to: 74, source: 'const' });
    it('a dropdown target offers its options as opt_<id>', async () => {
      const w = mountRow(constRow(), { targetField: field(74, 'enum', [opt(55), opt(56)]) });
      const options = comp(w, 'set-0-0-const')!.props('options') as { value: string }[];
      expect(options.map((o) => o.value)).toEqual(['opt_55', 'opt_56']);
    });

    it('a date target can use the business date', async () => {
      const w = mountRow(constRow(), { targetField: field(74, 'date') });
      comp(w, 'set-0-0-today')!.vm.$emit('update:modelValue', true);
      expect(lastEmit(w)).toMatchObject({ value: 'today' });
    });

    it('a number target sends a number', async () => {
      const w = mountRow(constRow(), { targetField: field(74, 'integer') });
      comp(w, 'set-0-0-const')!.vm.$emit('update:modelValue', '5');
      expect(lastEmit(w)).toMatchObject({ value: 5 });
    });
  });

  it('shows the server message and can be removed', async () => {
    const w = mountRow(emptySet(), { error: '目的欄位型別不相容' });
    expect(w.text()).toContain('目的欄位型別不相容');
    await w.find('[data-testid="set-0-0-remove"]').trigger('click');
    await flushPromises();
    expect(w.emitted('remove')).toHaveLength(1);
  });
});
