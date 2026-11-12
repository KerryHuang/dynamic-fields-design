import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DynamicRecord, ModuleSchema, SchemaDetail, SchemaField, SchemaWorkflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import DetailGrid from 'src/presentation/dynamic/details/DetailGrid.vue';
import DynamicField from './DynamicField.vue';
import DynamicForm from './DynamicForm.vue';
import DynamicDetail from './DynamicDetail.vue';

const base = { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false };
const field = (id: number, label: string, type: SchemaField['type'] = 'text'): SchemaField =>
  ({ id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [], config: { ...base } });
const status: SchemaField = {
  ...field(5, '狀態', 'enum'),
  options: [11, 12].map((n) => ({ value: `opt_${n}`, label: { 'zh-TW': `s${n}` }, active: true })),
};
const workflow: SchemaWorkflow = {
  statusFieldKey: 'f_5', initialState: 'opt_11',
  states: [{ value: 'opt_11', readOnly: false }, { value: 'opt_12', readOnly: false }],
  transitions: [{ from: 'opt_11', to: 'opt_12' }, { from: 'opt_12', to: 'opt_11' }],
};
const line: SchemaDetail = {
  moduleKey: 'line', name: { 'zh-TW': '明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_11', fields: [field(11, '品名')], rules: [],
};
const schema: ModuleSchema = {
  key: 'po', programCode: 'DYN_PO', name: { 'zh-TW': '採購單' }, titleFieldKey: 'f_1', version: 1, rules: [], workflow, details: [line],
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '名稱'), field(2, '金額', 'currency'), field(3, '備註'), field(4, '附件', 'attachment'), status] }],
};
const record = (lock: DynamicRecord['approvalLock']): DynamicRecord => ({
  id: 7, moduleKey: 'po', title: 'abc', values: { f_1: 'abc', f_2: 5, f_3: 'm', f_5: 'opt_12' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '',
  modifiedUserId: 'a', modifiedTime: '', rowVersion: 'rv1', references: {}, hostKey: null, approvalLock: lock,
});

function mountForm(props: Record<string, unknown>) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DynamicForm, {
    props: { schema, mode: 'edit', ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
}
const readonlyOf = (w: ReturnType<typeof mountForm>, key: string) =>
  w.findAllComponents(DynamicField).find((c) => c.props('field').key === key)!.props('readonly');

describe('DynamicForm approval lock', () => {
  beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });

  it('locked field is readonly with a lock icon and the hint', async () => {
    const w = mountForm({ record: record({ fieldKeys: ['f_1', 'f_2'], detailRowsLocked: false }) });
    await flushPromises();
    expect(readonlyOf(w, 'f_2')).toBe(true);
    const lock = w.find('[data-testid="approval-lock-f_2"]');
    expect(lock.exists()).toBe(true);
    expect(lock.text()).toContain('已經過簽核，要修改請先退回可編輯的狀態');
    expect(lock.find('.q-icon').exists()).toBe(true);
  });

  it('field outside the lock list stays editable and has no lock', async () => {
    const w = mountForm({ record: record({ fieldKeys: ['f_2'], detailRowsLocked: false }) });
    await flushPromises();
    expect(readonlyOf(w, 'f_3')).toBe(false);
    expect(w.find('[data-testid="approval-lock-f_3"]').exists()).toBe(false);
  });

  it('detail grid is readonly when detailRowsLocked, editable otherwise', async () => {
    const locked = mountForm({ record: record({ fieldKeys: [], detailRowsLocked: true }) });
    await flushPromises();
    expect(locked.findComponent(DetailGrid).props('readonly')).toBe(true);
    const open = mountForm({ record: record({ fieldKeys: [], detailRowsLocked: false }) });
    await flushPromises();
    expect(open.findComponent(DetailGrid).props('readonly')).toBe(false);
  });

  it('attachment field in the lock list is readonly', async () => {
    const w = mountForm({ record: record({ fieldKeys: ['f_4'], detailRowsLocked: false }) });
    await flushPromises();
    expect(readonlyOf(w, 'f_4')).toBe(true);
  });

  it('status field stays selectable on a locked record', async () => {
    const w = mountForm({ record: record({ fieldKeys: ['f_1', 'f_2', 'f_3'], detailRowsLocked: true }) });
    await flushPromises();
    expect(readonlyOf(w, 'f_5')).toBe(false);
    expect(w.find('[data-testid="approval-lock-f_5"]').exists()).toBe(false);
  });

  it('create mode and records without a lock show no lock', async () => {
    const a = mountForm({ record: record(null) });
    const b = mountForm({ mode: 'create', record: record({ fieldKeys: ['f_2'], detailRowsLocked: true }) });
    await flushPromises();
    expect(a.find('[data-testid^="approval-lock-"]').exists()).toBe(false);
    expect(b.find('[data-testid^="approval-lock-"]').exists()).toBe(false);
    expect(readonlyOf(b, 'f_2')).toBe(false);
  });
});

describe('DynamicDetail approval lock', () => {
  const mountDetail = (rec: DynamicRecord) => mount(DynamicDetail, {
    props: { schema, record: rec },
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }), [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });

  it('locked field label shows a lock icon with aria-label', () => {
    const w = mountDetail(record({ fieldKeys: ['f_2'], detailRowsLocked: false }));
    const lock = w.find('[data-testid="approval-lock-f_2"]');
    expect(lock.exists()).toBe(true);
    expect(lock.attributes('aria-label')).toBe('已經過簽核，要修改請先退回可編輯的狀態');
    expect(lock.attributes('title')).toBe('已經過簽核，要修改請先退回可編輯的狀態');
    expect(lock.attributes('aria-hidden')).not.toBe('true'); // QIcon hides itself from readers: the labelled wrapper must stay exposed
    expect(w.find('[data-testid="approval-lock-f_3"]').exists()).toBe(false);
  });

  it('no lock list: no icons', () => {
    expect(mountDetail(record(null)).find('[data-testid^="approval-lock-"]').exists()).toBe(false);
  });
});
