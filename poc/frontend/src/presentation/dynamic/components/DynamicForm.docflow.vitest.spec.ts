import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DynamicRecord, ModuleSchema, SchemaField, SchemaWorkflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DynamicField from './DynamicField.vue';
import DynamicForm from './DynamicForm.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getRecord: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false };
const text = (id: number, label: string, config: Partial<SchemaField['config']> = {}): SchemaField =>
  ({ id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [], config: { ...base, ...config } });
const status: SchemaField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, config: base,
  options: [11, 12, 13].map((n) => ({ value: `opt_${n}`, label: { 'zh-TW': `s${n}` }, active: true })),
};
const customer: SchemaField = {
  id: 10, key: 'f_10', type: 'reference', label: { 'zh-TW': '客戶' }, options: [],
  config: { ...base, refTarget: { kind: 'module', key: 'cust' }, fill: { mode: 'lock', map: [{ from: 31, to: 2 }, { from: 32, to: 3 }] } },
  reference: { kind: 'module', key: 'cust', name: { 'zh-TW': '客戶' }, lightUrl: '/modules/cust/light' },
};
const workflow: SchemaWorkflow = {
  statusFieldKey: 'f_5', initialState: 'opt_11',
  states: [{ value: 'opt_11', readOnly: false }, { value: 'opt_12', readOnly: true }, { value: 'opt_13', readOnly: true }],
  transitions: [{ from: 'opt_11', to: 'opt_12' }, { from: 'opt_12', to: 'opt_13' }],
};
const schema = (extra: Partial<ModuleSchema> = {}): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [],
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [
    text(1, '名稱'), text(2, '客戶名稱', { readOnly: true }), text(3, '付款條件'), status, customer,
  ] }], ...extra,
});
const record = (state: string): DynamicRecord => ({
  id: 7, moduleKey: 'm', title: 'abc', values: { f_1: 'abc', f_5: state }, ownerUserId: 'a', createdUserId: 'a', createdTime: '',
  modifiedUserId: 'a', modifiedTime: '', rowVersion: 'rv1', references: {}, hostKey: null,
});

function mountForm(props: Record<string, unknown>) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DynamicForm, {
    props: { schema: schema(), mode: 'create', ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
}
const fieldProps = (w: ReturnType<typeof mountForm>, key: string) =>
  w.findAllComponents(DynamicField).find((c) => c.props('field').key === key)!.props();

const allowed = (w: ReturnType<typeof mountForm>, key: string) => [...(fieldProps(w, key).allowedOptions as Set<string>)].sort();

describe('DynamicForm workflow', () => {
  beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });

  it('on create starts at the initial state and offers only it', async () => {
    const w = mountForm({ schema: schema({ workflow }) });
    await flushPromises();
    expect(allowed(w, 'f_5')).toEqual(['opt_11']);
    await w.find('form').trigger('submit');
    await vi.waitFor(() => expect(w.emitted('submit')).toBeTruthy());
    expect((w.emitted('submit')![0]![0] as Record<string, unknown>).f_5).toBe('opt_11');
  });

  it('on edit offers the current state plus the reachable ones', async () => {
    const w = mountForm({ schema: schema({ workflow }), mode: 'edit', record: record('opt_11') });
    await flushPromises();
    expect(allowed(w, 'f_5')).toEqual(['opt_11', 'opt_12']);
    expect(w.find('[data-testid="workflow-readonly"]').exists()).toBe(false);
  });

  it('a read-only state locks every field except the status, which can still advance', async () => {
    const w = mountForm({ schema: schema({ workflow }), mode: 'edit', record: record('opt_12') });
    await flushPromises();
    expect(w.find('[data-testid="workflow-readonly"]').text()).toBe(zhTW.docflow.readOnlyState);
    expect(fieldProps(w, 'f_1').readonly).toBe(true);
    expect(fieldProps(w, 'f_5').readonly).toBe(false);
    expect(allowed(w, 'f_5')).toEqual(['opt_12', 'opt_13']);
    expect(w.find('button[type="submit"]').exists()).toBe(true);
  });

  it('a read-only state with no way out has nothing to save', async () => {
    const w = mountForm({ schema: schema({ workflow }), mode: 'edit', record: record('opt_13') });
    await flushPromises();
    expect(w.find('button[type="submit"]').exists()).toBe(false);
  });

  it('without a workflow nothing changes', async () => {
    const w = mountForm({ mode: 'edit', record: record('opt_11') });
    await flushPromises();
    expect(fieldProps(w, 'f_5').allowedOptions).toBeUndefined();
    expect(fieldProps(w, 'f_1').readonly).toBe(false);
  });
});

describe('DynamicForm fill', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  const pick = async (w: ReturnType<typeof mountForm>, id: number | null) => {
    w.findComponent(PopupSelect).vm.$emit('update:modelValue', id);
    await flushPromises();
  };
  const submitted = async (w: ReturnType<typeof mountForm>) => {
    await w.find('form').trigger('submit');
    await vi.waitFor(() => expect(w.emitted('submit')).toBeTruthy());
    return w.emitted('submit')![0]![0] as Record<string, unknown>;
  };

  it('copies the picked record into the mapped fields and keeps lock targets read-only', async () => {
    api.getRecord.mockResolvedValue({ ...record('x'), id: 3, values: { f_31: 'Acme', f_32: 'Net 30' } });
    const w = mountForm({});
    await pick(w, 3);
    expect(api.getRecord).toHaveBeenCalledWith(3);
    expect(fieldProps(w, 'f_2').readonly).toBe(true);
    const values = await submitted(w);
    expect(values).toMatchObject({ f_2: 'Acme', f_3: 'Net 30', f_10: 3 });
  });

  it('clearing the reference clears the lock targets', async () => {
    api.getRecord.mockResolvedValue({ ...record('x'), id: 3, values: { f_31: 'Acme', f_32: 'Net 30' } });
    const w = mountForm({});
    await pick(w, 3);
    await pick(w, null);
    expect((await submitted(w)).f_2).toBeNull();
  });

  it('does not fetch when an existing record loads with its reference already set', async () => {
    const rec = { ...record('x'), values: { f_1: 'abc', f_10: 3, f_2: 'Acme' } };
    mountForm({ mode: 'edit', record: rec });
    await flushPromises();
    expect(api.getRecord).not.toHaveBeenCalled();
  });

  it('fills a prefilled reference on create', async () => {
    api.getRecord.mockResolvedValue({ ...record('x'), id: 3, values: { f_31: 'Acme' } });
    const w = mountForm({ initial: { f_10: 3 } });
    await flushPromises();
    expect(api.getRecord).toHaveBeenCalledWith(3);
    expect((await submitted(w)).f_2).toBe('Acme');
  });
});
