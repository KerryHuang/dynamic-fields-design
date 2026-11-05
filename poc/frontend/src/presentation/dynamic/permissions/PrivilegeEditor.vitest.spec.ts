import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { Group, ModulePermissions, Privileges } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import PrivilegeEditor from './PrivilegeEditor.vue';

const none: Privileges = { read: false, add: false, edit: false, delete: false, printExport: false, pricing: false, approve: false };
const groups: Group[] = [{ id: 1, key: 'sales', name: { 'zh-TW': '業務' }, members: [] }];
const users = [{ userId: 'u1', displayName: '甲' }, { userId: 'u2', displayName: '乙' }];
const base = (): ModulePermissions => ({
  groups: [{ groupId: 1, privileges: { ...none } }],
  users: [{ userId: 'u1', privileges: { ...none } }],
  fields: [{ fieldId: 9, groupId: 1, access: 'hidden' }],
  options: [],
});

function mountEditor(modelValue = base(), readonly = false) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(PrivilegeEditor, {
    props: { modelValue, groups, users, readonly },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}
// Quasar may put attrs on an inner element, so match the component whose DOM contains the test id.
const byId = (w: ReturnType<typeof mountEditor>, c: typeof quasar.QSelect | typeof quasar.QBtn, id: string) => {
  const el = w.find(`[data-testid="${id}"]`).element;
  return w.findAllComponents(c as never).find((x) => x.element.contains(el))! as unknown as { props(k: string): unknown; vm: { $emit(e: string, v: unknown): void } };
};
const last = (w: ReturnType<typeof mountEditor>) => w.emitted('update:modelValue')!.at(-1)![0] as ModulePermissions;

describe('PrivilegeEditor', () => {
  it('toggles a group axis and emits the updated permissions', async () => {
    const w = mountEditor();
    await w.find('[data-testid="g-1-edit"]').trigger('click');
    const out = last(w);
    expect(out.groups[0]!.privileges.edit).toBe(true);
    expect(out.groups[0]!.privileges.read).toBe(false);
    expect(out.fields).toEqual(base().fields);
  });

  it('sets scope own and clears it back to default (null)', async () => {
    const w = mountEditor();
    const sel = byId(w, quasar.QSelect, 'scope-1');
    sel.vm.$emit('update:modelValue', 'own');
    expect(last(w).groups[0]!.scope).toBe('own');
    sel.vm.$emit('update:modelValue', null);
    expect(last(w).groups[0]!.scope).toBeNull();
  });

  it('adds and removes a personal row', async () => {
    const w = mountEditor();
    byId(w, quasar.QSelect, 'user-add').vm.$emit('update:modelValue', 'u2');
    expect(last(w).users.map((u) => u.userId)).toEqual(['u1', 'u2']);
    await w.find('[data-testid="user-remove-u1"]').trigger('click');
    expect(last(w).users.map((u) => u.userId)).toEqual([]);
  });

  it('is fully disabled when readonly', async () => {
    const w = mountEditor(base(), true);
    await w.find('[data-testid="g-1-edit"]').trigger('click');
    expect(w.emitted('update:modelValue')).toBeUndefined();
    for (const c of w.findAllComponents(quasar.QCheckbox)) expect(c.props('disable')).toBe(true);
    expect(byId(w, quasar.QSelect, 'scope-1').props('disable')).toBe(true);
    expect(byId(w, quasar.QSelect, 'user-add').props('disable')).toBe(true);
    expect(byId(w, quasar.QBtn, 'user-remove-u1').props('disable')).toBe(true);
  });

  it('user-add 挑完後輸入框清空，再開啟顯示全部可加入的使用者', async () => {
    const w = mount(PrivilegeEditor, {
      props: { modelValue: base(), groups, users: [...users, { userId: 'u3', displayName: '丙' }], readonly: false },
      global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
      attachTo: document.body,
    });
    const el = w.get('[data-testid="user-add"]').element;
    const inputEl = (el.tagName === 'INPUT' ? el : el.closest('label')!.querySelector('input')!) as HTMLInputElement;
    const input = w.get('[data-testid="user-add"]').element.tagName === 'INPUT' ? w.get('[data-testid="user-add"]') : w.findAll('input').find((i) => i.element === inputEl)!;
    const items = () => [...document.body.querySelectorAll('.q-item')].map((e) => e.textContent?.trim());
    const wait = async () => { await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
    inputEl.focus();
    await input.setValue('乙');
    await wait();
    expect(items()).toEqual(['乙 (u2)']);
    (document.body.querySelector('.q-item') as HTMLElement).click();
    await wait();
    expect(inputEl.value).toBe('');
    (w.findAllComponents(quasar.QSelect).find((c) => c.element.contains(el))!.vm as unknown as { showPopup(): void }).showPopup();
    await wait();
    expect(items()).toEqual(['乙 (u2)', '丙 (u3)']);
    w.unmount();
  });
});
