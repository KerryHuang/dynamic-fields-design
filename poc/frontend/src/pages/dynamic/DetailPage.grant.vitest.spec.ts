import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ApprovalInstanceView, RecordApproval } from 'src/domain/dynamic/approval-types';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import DetailPage from './DetailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getRecord: vi.fn(), deleteRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn(), getRecordTasks: vi.fn(),
    getRecordApproval: vi.fn(), getEffectivePolicies: vi.fn(), getRecordConversions: vi.fn(), getRecordEffects: vi.fn(), getRecordLedger: vi.fn(),
    printRecord: vi.fn(), approveTask: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const base = { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true };
const title: SchemaField = { id: 1, key: 'f_1', type: 'text', label: { 'zh-TW': '標題' }, options: [], config: base };
const amount: SchemaField = { id: 2, key: 'f_2', type: 'text', label: { 'zh-TW': '核定金額' }, options: [], config: base };
const grantSchema = (): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '採購單' }, titleFieldKey: 'f_1', version: 1, rules: [],
  permissions: { read: false, add: false, edit: false, delete: false, printExport: false, pricing: false },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [title, amount] }],
});
const record = (grant: boolean): DynamicRecord => ({
  id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五', f_2: 'old' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a',
  modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null, approvalGrant: grant,
  approval: { status: 'pending', stageName: null, stageSeq: 1, canSubmit: false },
});
const instance = (p: Partial<ApprovalInstanceView> = {}): ApprovalInstanceView => ({
  id: 7, status: 'pending', routeKind: 'flow', flowName: { 'zh-TW': '採購簽核' }, fromOptionId: 11, toOptionId: 12, initiatorUserId: 'a',
  submittedTime: '2026-11-12T08:00:00', completedTime: null, rowVersion: 'iv', canWithdraw: false, canCancel: false, titleSnapshot: '記錄五', actions: [],
  stages: [{
    id: 1, seq: 1, stageCode: 's1', name: { 'zh-TW': '財務' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, editableFieldKeys: ['f_2'],
    tasks: [{ id: 11, assigneeUserId: 'fin', assigneeName: '財務', originalUserId: 'fin', reason: 'resolved', status: 'open', actedTime: null, canAct: true, canReturnPrevious: false }],
  }],
  ...p,
});
const approvalOf = (current: ApprovalInstanceView | null, previous: ApprovalInstanceView[] = []): RecordApproval => ({ current, previous, submitTargets: [] });
const gone = (status: number) => Promise.reject({ status });

let qc: QueryClient;
async function mountPage(setup: () => void) {
  vi.clearAllMocks();
  setActivePinia(createPinia());
  useIdentityStore().setSession('fin');
  api.related.mockResolvedValue([] as never);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  api.getRecordTasks.mockResolvedValue({ items: [], canAdd: false, manualRules: [] });
  api.getEffectivePolicies.mockResolvedValue({ values: {} });
  api.getRecordConversions.mockResolvedValue([]);
  api.getRecordEffects.mockResolvedValue({ items: [], hiddenCount: 0 } as never);
  setup();
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: qc }]],
    },
  });
  await flushPromises();
  await new Promise((r) => setTimeout(r, 20));
  await flushPromises();
  return w;
}
const settle = async () => { await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
const dlg = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
const has = (w: { find: (s: string) => { exists: () => boolean } }, id: string) => w.find(`[data-testid="${id}"]`).exists();

describe('DetailPage temporary read', () => {
  beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });

  it('grant user sees record and actions, related panels hidden', async () => {
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation((_k, recordId) => (recordId === undefined ? gone(403) : Promise.resolve(grantSchema())));
      api.getRecord.mockResolvedValue(record(true));
      api.getRecordApproval.mockResolvedValue(approvalOf(instance()));
    });
    expect(api.getModuleSchema).toHaveBeenCalledWith('m', 5);
    expect(w.text()).toContain('old');
    expect(has(w, 'forbidden')).toBe(false);
    expect(has(w, 'approval-grant-banner')).toBe(true);
    expect(has(w, 'action-approve')).toBe(true);
    (w.find('[data-testid="action-approve"]').element as HTMLElement).click();
    await settle();
    expect(dlg('approve-editable')!.textContent).toContain('核定金額');
    expect(api.related).not.toHaveBeenCalled();
    expect(api.recordHistory).not.toHaveBeenCalled();
    expect(api.getRecordTasks).not.toHaveBeenCalled();
    expect(api.getRecordConversions).not.toHaveBeenCalled();
    expect(api.getRecordEffects).not.toHaveBeenCalled();
    expect(w.text()).not.toContain('列印');
    expect(w.text()).not.toContain(zhTW.dynamic.edit);
    expect(w.text()).not.toContain(zhTW.dynamic.delete);
    w.unmount(); // the dialog is teleported to body: do not leak it into the next test
  });

  it('record 404 on first load shows history only', async () => {
    const ended = instance({ status: 'approved', completedTime: '2026-11-12T09:00:00' });
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(404));
      api.getRecordApproval.mockResolvedValue(approvalOf(null, [ended]));
    });
    expect(has(w, 'approval-history-only')).toBe(true);
    expect(has(w, 'forbidden')).toBe(false);
    expect(has(w, 'approval-panel')).toBe(true);
    expect(has(w, 'action-approve')).toBe(false);
  });

  it('approving with the temporary right turns the same page into history only', async () => {
    const ended = instance({ status: 'approved', completedTime: '2026-11-12T09:00:00' });
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation((_k, recordId) => (recordId === undefined ? gone(403) : Promise.resolve(grantSchema())));
      api.getRecord.mockResolvedValue(record(true));
      api.getRecordApproval.mockResolvedValue(approvalOf(instance()));
      api.approveTask.mockImplementation(async () => {
        // The task ended with the approval: the record is out of reach, the history is not.
        api.getRecord.mockImplementation(() => gone(403));
        api.getRecordApproval.mockResolvedValue(approvalOf(null, [ended]));
        return { outcome: 'approved', instanceId: 7, instanceRowVersion: 'iv2' } as never;
      });
    });
    expect(has(w, 'approval-grant-banner')).toBe(true);
    (w.find('[data-testid="action-approve"]').element as HTMLElement).click();
    await settle();
    dlg('approve-send')!.click();
    await settle();
    expect(api.approveTask).toHaveBeenCalledTimes(1);
    expect(has(w, 'approval-history-only')).toBe(true);
    expect(has(w, 'approval-grant-banner')).toBe(false);
    expect(has(w, 'forbidden')).toBe(false);
  });

  it('task closed by someone else: stale approval data is not shown as history', async () => {
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation((_k, recordId) => (recordId === undefined ? gone(403) : Promise.resolve(grantSchema())));
      api.getRecord.mockResolvedValue(record(true));
      api.getRecordApproval.mockResolvedValue(approvalOf(instance()));
    });
    expect(has(w, 'approval-grant-banner')).toBe(true);
    api.getRecord.mockImplementation(() => gone(403));
    api.getRecordApproval.mockImplementation(() => gone(404));
    await qc.invalidateQueries();
    await settle();
    expect(has(w, 'forbidden')).toBe(true);
    expect(has(w, 'approval-history-only')).toBe(false);
  });

  it('record 403 and approval 404 shows the forbidden notice', async () => {
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(403));
      api.getRecordApproval.mockImplementation(() => gone(404));
    });
    expect(has(w, 'forbidden')).toBe(true);
    expect(has(w, 'approval-history-only')).toBe(false);
  });

  it('history only shows the title snapshot and no actions', async () => {
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(403));
      api.getRecordApproval.mockResolvedValue(approvalOf(null, [instance({ status: 'approved', titleSnapshot: '快照標題' })]));
    });
    expect(w.find('[data-testid="approval-history-only"]').text()).toContain('快照標題');
    expect(has(w, 'action-approve')).toBe(false);
    expect(has(w, 'action-reject')).toBe(false);
    expect(has(w, 'action-withdraw')).toBe(false);
  });

  it('history only: the initiator can still withdraw a running approval, another user cannot', async () => {
    const running = () => approvalOf(instance({ canWithdraw: true, initiatorUserId: 'fin' }));
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(403));
      api.getRecordApproval.mockResolvedValue(running());
    });
    expect(has(w, 'approval-history-only')).toBe(true);
    expect((w.find('[data-testid="action-withdraw"]').element as HTMLButtonElement).disabled).toBe(false);
    const other = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(403));
      api.getRecordApproval.mockResolvedValue(approvalOf(instance({ canWithdraw: true, initiatorUserId: 'someone', stages: [] })));
    });
    expect(has(other, 'action-withdraw')).toBe(false);
  });

  it('history only with an empty snapshot shows the record number instead of a blank title', async () => {
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(403));
      api.getRecordApproval.mockResolvedValue(approvalOf(null, [instance({ status: 'approved', titleSnapshot: '' })]));
    });
    expect(w.find('[data-testid="approval-history-only"]').text()).toContain('#5');
  });

  it('user with read does not request the record-scoped schema', async () => {
    const full = { ...grantSchema(), permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true } };
    await mountPage(() => {
      api.getModuleSchema.mockResolvedValue(full);
      api.getRecord.mockResolvedValue(record(false));
      api.getRecordApproval.mockResolvedValue(approvalOf(instance()));
    });
    expect(api.getModuleSchema).toHaveBeenCalledTimes(1);
    expect(api.getModuleSchema).toHaveBeenCalledWith('m');
  });

  it('a record without read that has no approval stays forbidden when the record request is 403 and nothing was asked of approval', async () => {
    const w = await mountPage(() => {
      api.getModuleSchema.mockImplementation(() => gone(403));
      api.getRecord.mockImplementation(() => gone(403));
      api.getRecordApproval.mockImplementation(() => gone(403));
    });
    expect(has(w, 'forbidden')).toBe(true);
  });
});
