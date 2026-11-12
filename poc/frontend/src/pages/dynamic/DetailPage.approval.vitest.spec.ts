import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { RecordApprovalSummary } from 'src/domain/dynamic/approval-types';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DetailPage from './DetailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getRecord: vi.fn(), deleteRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn(), getRecordTasks: vi.fn(),
    getRecordApproval: vi.fn(), previewApproval: vi.fn(), getEffectivePolicies: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true };
const title: SchemaField = { id: 1, key: 'f_1', type: 'text', label: { 'zh-TW': '標題' }, options: [], config: base };
const status: SchemaField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, config: base,
  options: [11, 12, 13].map((n) => ({ value: `opt_${n}`, label: { 'zh-TW': `狀態${n}` }, active: true })),
};
const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [],
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [title, status] }],
  workflow: {
    statusFieldKey: 'f_5', initialState: 'opt_11',
    states: [{ value: 'opt_11', readOnly: false }, { value: 'opt_12', readOnly: true }, { value: 'opt_13', readOnly: true }],
    transitions: [{ from: 'opt_11', to: 'opt_12', approval: true }, { from: 'opt_11', to: 'opt_13', approval: true }],
  },
};
const record = (approval?: RecordApprovalSummary | null): DynamicRecord => ({
  id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五', f_5: 'opt_11' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a',
  modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null, approval,
});
const summary = (p: Partial<RecordApprovalSummary>): RecordApprovalSummary => ({ status: 'none', stageName: null, stageSeq: null, canSubmit: false, ...p });

async function mountPage(rec: DynamicRecord, targets: number[] = []) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(schema);
  api.getRecord.mockResolvedValue(rec);
  api.related.mockResolvedValue([] as never);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  api.getRecordTasks.mockResolvedValue({ items: [], canAdd: false, manualRules: [] });
  api.getRecordApproval.mockResolvedValue({ current: null, previous: [], submitTargets: targets });
  api.getEffectivePolicies.mockResolvedValue({ values: {} });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

describe('DetailPage approval', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('shows pending badge with stage seq and name', async () => {
    const w = await mountPage(record(summary({ status: 'pending', stageSeq: 2, stageName: { 'zh-TW': '財務' } })));
    expect(w.find('[data-testid="approval-badge"]').text()).toBe('簽核中（第 2 關／財務）');
  });

  it('no approval summary: no badge and no approval request', async () => {
    const w = await mountPage(record(null));
    expect(w.find('[data-testid="approval-badge"]').exists()).toBe(false);
    expect(api.getRecordApproval).not.toHaveBeenCalled();
    expect(w.text()).toContain(zhTW.dynamic.edit);
  });

  it('hides edit and delete while pending and shows read-only banner', async () => {
    const w = await mountPage(record(summary({ status: 'pending', stageSeq: 1, stageName: { 'zh-TW': '主管' } })));
    expect(w.text()).not.toContain(zhTW.dynamic.edit);
    expect(w.text()).not.toContain(zhTW.dynamic.delete);
    expect(w.find('[data-testid="approval-pending-readonly"]').text()).toContain('簽核中，記錄唯讀');
  });

  it('one submit button per submit target, labelled with the state name', async () => {
    const w = await mountPage(record(summary({ status: 'none', canSubmit: true })), [12, 13]);
    const buttons = w.findAll('[data-testid^="submit-approval-"]');
    expect(buttons).toHaveLength(2);
    expect(buttons[0]!.text()).toContain('送出簽核：狀態12');
    expect(buttons[1]!.text()).toContain('送出簽核：狀態13');
    expect(api.getRecordApproval).toHaveBeenCalledWith(5);
  });

  it('does not ask for submit targets when the user cannot submit', async () => {
    const w = await mountPage(record(summary({ status: 'approved' })));
    expect(w.findAll('[data-testid^="submit-approval-"]')).toHaveLength(0);
  });

  it('approved summary shows positive badge', async () => {
    const w = await mountPage(record(summary({ status: 'approved' })));
    const badge = w.find('[data-testid="approval-badge"]');
    expect(badge.text()).toBe('已核准');
    expect(badge.classes().join(' ')).toContain('bg-positive');
  });
});
