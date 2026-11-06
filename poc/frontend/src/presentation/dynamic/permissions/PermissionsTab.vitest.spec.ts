import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { createPinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { Me, ModulePermissions, Privileges } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import PermissionsTab from './PermissionsTab.vue';
import { FACTORY_ME_THEME } from 'src/domain/theme/theme-fixtures';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModulePermissions: vi.fn(), saveModulePermissions: vi.fn(), getGroups: vi.fn(), getUsers: vi.fn(), getMe: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const none: Privileges = { read: false, add: false, edit: false, delete: false, printExport: false, pricing: false, approve: false };
const permsFor = (scope: 'own' | null): ModulePermissions => ({
  groups: [{ groupId: 1, privileges: { ...none, read: scope === null } }],
  users: [],
  fields: [{ fieldId: 9, groupId: 1, access: 'hidden' }],
  options: [{ optionId: 4, groupIds: [1] }],
});
const me = (edit: boolean): Me => ({
  userId: 'a', displayName: 'A', isSystemAdmin: false, mustChangePassword: false, groups: [], designer: { read: true, add: true, edit, delete: false }, theme: FACTORY_ME_THEME,
});

function mountTab(canEdit = true, perms?: ModulePermissions, extraProps: Record<string, unknown> = {}) {
  api.getModulePermissions.mockImplementation(async (k) => perms ?? permsFor(k === 'a' ? null : 'own'));
  api.getGroups.mockResolvedValue([{ id: 1, key: 'sales', name: { 'zh-TW': '業務' }, members: [] }]);
  api.getUsers.mockResolvedValue([]);
  api.getMe.mockResolvedValue(me(canEdit));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(PermissionsTab, {
    props: { moduleKey: 'a', ...extraProps },
    global: {
      plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n, createPinia(),
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
}

describe('PermissionsTab', () => {
  beforeEach(() => vi.clearAllMocks());

  it('leaves loading and renders the rows from real query data', async () => {
    const w = mountTab();
    await flushPromises();
    expect(w.find('[data-testid="group-row-1"]').exists()).toBe(true);
  });

  it('PUTs the whole object with fields and options unchanged, then notifies saved', async () => {
    const w = mountTab();
    await flushPromises();
    api.saveModulePermissions.mockResolvedValue(permsFor(null));
    await w.find('[data-testid="g-1-edit"]').trigger('click');
    await w.find('[data-testid="save-permissions"]').trigger('click');
    await flushPromises();
    const [key, body] = api.saveModulePermissions.mock.calls[0]!;
    expect(key).toBe('a');
    expect(body.groups[0]!.privileges.edit).toBe(true);
    expect(body.fields).toEqual(permsFor(null).fields);
    expect(body.options).toEqual(permsFor(null).options);
    await vi.waitFor(() => expect(document.body.textContent).toContain(zhTW.designer.permissions.saved));
  });

  it('marks the group row named by a 400 error key', async () => {
    const w = mountTab();
    await flushPromises();
    api.saveModulePermissions.mockRejectedValue({ status: 400, errors: { 'groups[0].scope': ['範圍不正確'] } });
    await w.find('[data-testid="save-permissions"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="group-error-1"]').text()).toBe('範圍不正確');
  });

  it('marks the cell of fields[1] (not display row 1) and clears marks after any edit', async () => {
    const fld = (id: number) => ({ id, key: `f_${id}`, type: 'text', label: { 'zh-TW': `欄${id}` }, config: {}, blockId: 1, sequence: id, status: 'Active', options: [] });
    // Sent order: field 12 first, then field 11; display order is 11, 12.
    const perms: ModulePermissions = { ...permsFor(null), fields: [
      { fieldId: 12, groupId: 1, access: 'readOnly' }, { fieldId: 11, groupId: 1, access: 'readOnly' },
    ], options: [] };
    const w = mountTab(true, perms, { fields: [fld(11), fld(12)] });
    await flushPromises();
    api.saveModulePermissions.mockRejectedValue({ status: 400, errors: { 'fields[1].access': ['bad'] } });
    await w.find('[data-testid="save-permissions"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="fp-error-11-1"]').exists()).toBe(true);
    expect(w.find('[data-testid="fp-error-12-1"]').exists()).toBe(false);
    const el = w.find('[data-testid="fp-12-1"]').element;
    const s = w.findAllComponents(quasar.QSelect as never).find((x) => x.element.contains(el))! as unknown as { vm: { $emit(e: string, v: unknown): void } };
    s.vm.$emit('update:modelValue', 'default');
    await flushPromises();
    expect(w.find('[data-testid^="fp-error-"]').exists()).toBe(false);
  });

  it('disables save without designer.edit', async () => {
    const w = mountTab(false);
    await flushPromises();
    expect(w.findComponent(quasar.QBtn).props('disable')).toBe(true);
  });

  it('drops the previous module draft when moduleKey changes', async () => {
    const w = mountTab();
    await flushPromises();
    await w.find('[data-testid="g-1-edit"]').trigger('click');
    await w.setProps({ moduleKey: 'b' });
    await flushPromises();
    api.saveModulePermissions.mockResolvedValue(permsFor('own'));
    await w.find('[data-testid="save-permissions"]').trigger('click');
    await flushPromises();
    const [key, body] = api.saveModulePermissions.mock.calls[0]!;
    expect(key).toBe('b');
    expect(body.groups[0]!.privileges.edit).toBe(false);
    expect(body.groups[0]!.privileges.read).toBe(false); // module b's data, not a's
  });

  it('detail module hides privilege matrix and scope, shows inherited note; field exceptions stay editable and groups/users are sent empty', async () => {
    const w = mountTab(true, undefined, { detailOf: 'order' });
    await flushPromises();
    expect(w.find('[data-testid="group-row-1"]').exists()).toBe(false);
    expect(w.text()).toContain('明細模組的權限沿用表頭模組');
    expect(w.findComponent({ name: 'FieldPermissionEditor' }).exists()).toBe(true);
    api.saveModulePermissions.mockResolvedValue(permsFor(null));
    await w.find('[data-testid="save-permissions"]').trigger('click');
    await flushPromises();
    const body = api.saveModulePermissions.mock.calls[0]![1];
    expect(body.groups).toEqual([]);
    expect(body.users).toEqual([]);
    expect(body.fields).toEqual(permsFor(null).fields);
    expect(body.options).toEqual(permsFor(null).options);
  });
});
