import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, Group, ModulePermissions } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import FieldPermissionEditor from './FieldPermissionEditor.vue';

const groups: Group[] = [
  { id: 1, key: 'sales', name: { 'zh-TW': '業務' }, members: [] },
  { id: 2, key: 'floor', name: { 'zh-TW': '現場' }, members: [] },
  { id: 3, key: 'mgr', name: { 'zh-TW': '主管' }, members: [] },
];
const field = (id: number, type: DesignerField['type'], extra: Partial<DesignerField> = {}): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, config: {} as never, blockId: 1, sequence: id, status: 'Active', options: [], ...extra,
});
const opt = (id: number) => ({ id, value: `opt_${id}`, label: { 'zh-TW': `選${id}` }, sequence: id, active: true });
const fields: DesignerField[] = [
  field(10, 'text'),
  field(11, 'currency'),
  field(12, 'enum', { options: [opt(21), opt(22)] }),
  field(13, 'multienum', { options: [opt(23)] }),
  field(14, 'enum', { status: 'Inactive', options: [opt(24)] }),
];
const base = (): ModulePermissions => ({ groups: [], users: [], fields: [], options: [] });

function mountEditor(modelValue = base(), errors?: Record<string, string[]>) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(FieldPermissionEditor, {
    props: { modelValue, fields, groups, titleFieldId: 10, readonly: false, errors },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}
const sel = (w: ReturnType<typeof mountEditor>, id: string) => {
  const el = w.find(`[data-testid="${id}"]`).element;
  return w.findAllComponents(quasar.QSelect as never).find((x) => x.element.contains(el))! as unknown as {
    props(k: string): unknown; vm: { $emit(e: string, v: unknown): void };
  };
};
const last = (w: ReturnType<typeof mountEditor>) => w.emitted('update:modelValue')!.at(-1)![0] as ModulePermissions;

describe('FieldPermissionEditor', () => {
  it('sets a cell to hidden and back to default removes the row', async () => {
    const w = mountEditor();
    sel(w, 'fp-11-1').vm.$emit('update:modelValue', 'hidden');
    expect(last(w).fields).toEqual([{ fieldId: 11, groupId: 1, access: 'hidden' }]);
    await w.setProps({ modelValue: last(w) });
    expect(sel(w, 'fp-11-1').props('modelValue')).toBe('hidden');
    sel(w, 'fp-11-1').vm.$emit('update:modelValue', 'default');
    expect(last(w).fields).toEqual([]);
  });

  it('does not offer hidden for the title field', () => {
    const w = mountEditor();
    const values = (id: string) => (sel(w, id).props('options') as { value: string }[]).map((o) => o.value);
    expect(values('fp-10-1')).toEqual(['default', 'readOnly']);
    expect(values('fp-11-1')).toEqual(['default', 'readOnly', 'hidden']);
  });

  it('scopes an option to two groups and clearing removes the row', async () => {
    const w = mountEditor();
    sel(w, 'opt-21').vm.$emit('update:modelValue', [1, 2]);
    expect(last(w).options).toEqual([{ optionId: 21, groupIds: [1, 2] }]);
    await w.setProps({ modelValue: last(w) });
    sel(w, 'opt-21').vm.$emit('update:modelValue', []);
    expect(last(w).options).toEqual([]);
  });

  it('lists only enum and multienum options (of active fields)', () => {
    const w = mountEditor();
    for (const id of [21, 22, 23]) expect(w.find(`[data-testid="opt-${id}"]`).exists()).toBe(true);
    expect(w.find('[data-testid="opt-24"]').exists()).toBe(false);
    expect(w.find('[data-testid="fp-12-1"]').exists()).toBe(true);
    expect(w.find('[data-testid="fp-14-1"]').exists()).toBe(false);
  });

  it('keeps a hidden option for a title field that already has a hidden row', () => {
    const m = base();
    m.fields = [{ fieldId: 10, groupId: 1, access: 'hidden' }];
    const w = mountEditor(m);
    const opts = sel(w, 'fp-10-1').props('options') as { value: string; label: string }[];
    expect(opts.find((o) => o.value === 'hidden')?.label).toBe(zhTW.designer.permissions.access.hidden);
    expect((sel(w, 'fp-10-2').props('options') as { value: string }[]).map((o) => o.value)).toEqual(['default', 'readOnly']);
  });

  it('marks cells and option rows from fields[i]/options[i] errors', () => {
    const m = base();
    m.fields = [{ fieldId: 10, groupId: 2, access: 'hidden' }];
    m.options = [{ optionId: 22, groupIds: [1] }];
    const w = mountEditor(m, { 'fields[0].access': ['標題欄位不能設為隱藏'], 'options[0].groupIds': ['群組不存在'] });
    expect(w.find('[data-testid="fp-error-10-2"]').text()).toContain('標題欄位不能設為隱藏');
    expect(w.find('[data-testid="opt-error-22"]').text()).toContain('群組不存在');
  });

  describe('contributed fields on a shared master', () => {
    const withGrants = (): ModulePermissions => ({
      ...base(), sharedMaster: true,
      fieldGrants: [{ fieldId: 11, groupIds: [3], groups: [{ groupId: 1, source: 'app', appKey: 'crm' }, { groupId: 3, source: 'manual', appKey: null }] }],
    });
    const box = (w: ReturnType<typeof mountEditor>, id: string) =>
      w.findAllComponents(quasar.QCheckbox).find((c) => c.attributes('data-testid') === id || c.find(`[data-testid="${id}"]`).exists())!;

    it('lists only restricted fields; app-given groups are ticked and locked, manual ones can be unticked', () => {
      const w = mountEditor(withGrants());
      expect(w.find('[data-testid="field-grants"]').text()).toContain('欄11');
      expect(w.find('[data-testid="field-grants"]').text()).not.toContain('欄10');
      expect(box(w, 'fg-11-1').props('modelValue')).toBe(true);
      expect(box(w, 'fg-11-1').props('disable')).toBe(true);
      expect(box(w, 'fg-11-1').text()).toContain('（來自 crm）');
      expect(box(w, 'fg-11-2').props('modelValue')).toBe(false);
      expect(box(w, 'fg-11-3').props('modelValue')).toBe(true);
      expect(box(w, 'fg-11-3').props('disable')).toBe(false);
    });

    it('ticking and unticking a group edits only the manual group ids', async () => {
      const w = mountEditor(withGrants());
      box(w, 'fg-11-2').vm.$emit('update:modelValue', true);
      expect(last(w).fieldGrants![0]!.groupIds).toEqual([3, 2]);
      await w.setProps({ modelValue: last(w) });
      box(w, 'fg-11-3').vm.$emit('update:modelValue', false);
      expect(last(w).fieldGrants![0]!.groupIds).toEqual([2]);
      expect(last(w).fieldGrants![0]!.groups!.map((g) => g.groupId)).toEqual([1, 2]);
    });

    it('has no visibility section on an ordinary module', () => {
      expect(mountEditor().find('[data-testid="field-grants"]').exists()).toBe(false);
    });
  });
});
