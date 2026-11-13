import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { DesignerField, ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import FlowEditorDialog from './FlowEditorDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getUsers: vi.fn(), getGroups: vi.fn(), getEffectivePolicies: vi.fn(), getModulePolicies: vi.fn(), createApprovalFlow: vi.fn(), updateApprovalFlow: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, type: string, label: string, options: DesignerField['options'] = []): DesignerField => ({
  id, key: `f_${id}`, type: type as never, label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options,
});
const mod: ModuleDetail = {
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{
    id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2,
    fields: [field(5, 'enum', '狀態', [11, 12, 13].map((n) => ({ id: n, value: `o${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: true }))), field(7, 'decimal', '總額')],
  }],
};
const wf: WorkflowResponse = {
  statusFieldId: 5, initialOptionId: 11,
  states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }, { optionId: 13, readOnly: true }],
  transitions: [{ from: 11, to: 12, groupIds: [], approval: { required: true, noMatch: 'block' } }, { from: 12, to: 13, groupIds: [] }],
  editableFieldIds: [7],
};
const flow = (key: string, to: number, priority: number, extra: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key, name: { 'zh-TW': `流程${key}` }, toOptionId: to, priority, active: true, moduleKey: 'doc', version: 2, used: false, pendingCount: 0,
  definition: { shape: 'sequence', when: null, rejectTo: null, fallbackApprovers: null, stages: [{ code: 'stage-1', name: { 'zh-TW': 'A' }, when: null, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], dueHours: null, onEmpty: 'fallback' }] },
  ...extra,
});

interface Opts { flow?: ApprovalFlow; flows?: ApprovalFlow[]; presetTarget?: number | null; users?: 'pending' | 'ok' }
async function mountDialog(o: Opts = {}) {
  api.getUsers.mockImplementation(() => (o.users === 'pending' ? new Promise(() => {}) : Promise.resolve([{ userId: 'u1', displayName: '王小明' }] as never)));
  api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管群' }, members: [] }]);
  api.getEffectivePolicies.mockResolvedValue({ values: { 'approval.maxStages': 5, 'approval.maxApprovers': 20, 'approval.maxDueHours': 100, 'approval.maxManagerLevel': 3 } } as never);
  api.getModulePolicies.mockResolvedValue([
    { code: 'approval.maxApprovers', kind: 'integer', max: 50, effective: 20 },
    { code: 'approval.defaultFallbackApprovers', kind: 'recipients', effective: ['user:u1'] },
  ] as never);
  api.createApprovalFlow.mockResolvedValue({ version: 1 } as never);
  api.updateApprovalFlow.mockResolvedValue({ version: o.flow?.version ?? 1 } as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(FlowEditorDialog, {
    attachTo: document.body,
    props: { modelValue: true, module: mod, workflow: wf, flows: o.flows ?? [], flow: o.flow, presetTarget: o.presetTarget },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  notify = vi.fn();
  (w.vm as unknown as { $q: { notify: unknown } }).$q.notify = notify;
  await flushPromises();
  return w;
}
type W = Awaited<ReturnType<typeof mountDialog>>;
let notify: ReturnType<typeof vi.fn>;
const body = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const text = (id: string) => body(id)?.textContent ?? '';
async function setInput(id: string, v: string) {
  const el = body(id) as HTMLInputElement;
  el.value = v;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
const select = (w: W, label: string) => w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === label)!;
async function click(id: string) { body(id)!.click(); await flushPromises(); }
async function fillValid(w: W) {
  await setInput('flow-key', 'doc-review');
  const nameEl = document.body.querySelector<HTMLInputElement>('[data-testid="i18n-zh-TW"] input')!;
  nameEl.value = '單據簽核';
  nameEl.dispatchEvent(new Event('input'));
  select(w, '目標狀態').vm.$emit('update:modelValue', 12);
  await flushPromises();
}

describe('FlowEditorDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    quasar.Screen.setDebounce(0);
    window.innerWidth = 1024;
    window.dispatchEvent(new Event('resize'));
  });

  it('create posts key, name, target, next priority, active and a sequence definition', async () => {
    const w = await mountDialog({ flows: [flow('a', 12, 1), flow('b', 12, 4)] });
    await fillValid(w);
    await click('flow-save');
    expect(api.createApprovalFlow).toHaveBeenCalledTimes(1);
    const [m, input] = api.createApprovalFlow.mock.calls[0]!;
    expect(m).toBe('doc');
    expect(input).toMatchObject({ key: 'doc-review', name: { 'zh-TW': '單據簽核' }, toOptionId: 12, priority: 5, active: true });
    expect(input.definition.shape).toBe('sequence');
    expect(input.definition.stages).toHaveLength(1);
    expect(input.definition.stages[0]!.code).toBe('stage-1');
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([false]);
    w.unmount();
  });

  it('empty start condition is saved as null', async () => {
    const w = await mountDialog();
    await fillValid(w);
    await click('flow-save');
    expect(api.createApprovalFlow.mock.calls[0]![1].definition.when).toBeNull();
    w.unmount();
  });

  it('saving without a target keeps the dialog open and asks for one', async () => {
    const w = await mountDialog();
    await setInput('flow-key', 'x-flow');
    const nameEl = document.body.querySelector<HTMLInputElement>('[data-testid="i18n-zh-TW"] input')!;
    nameEl.value = 'n';
    nameEl.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('flow-save');
    expect(api.createApprovalFlow).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('請選擇目標狀態');
    w.unmount();
  });

  it('edit locks the key; a used flow disables the target with the reason', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1, { used: true, currentVersionUsed: true }) });
    expect((body('flow-key') as HTMLInputElement).readOnly).toBe(true);
    const target = select(w, '目標狀態');
    expect(target.props('disable')).toBe(true);
    expect(document.body.textContent).toContain('被使用過的流程不能改目標狀態');
    w.unmount();
  });

  it('target options exclude the initial state and mark states without approval entries', async () => {
    const w = await mountDialog();
    const opts = select(w, '目標狀態').props('options') as { value: number; label: string }[];
    expect(opts.map((o) => o.value)).toEqual([12, 13]);
    expect(opts[0]!.label).toBe('狀態12（已有需簽核的入口）');
    expect(opts[1]!.label).toBe('狀態13（還沒有需簽核的入口）');
    w.unmount();
  });

  it('used current version shows the new-version banner with the pending count', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1, { used: true, currentVersionUsed: true, pendingCount: 3, version: 2 }) });
    expect(text('flow-banner')).toBe('儲存會產生第 3 版，進行中的 3 張單照舊版本走完');
    w.unmount();
  });

  it('zero pending uses the not-affected variant', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1, { used: true, currentVersionUsed: true, pendingCount: 0, version: 2 }) });
    expect(text('flow-banner')).toBe('儲存會產生第 3 版，已送出的單不受影響');
    w.unmount();
  });

  it('unused current version shows no banner', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1, { used: true, currentVersionUsed: false }) });
    expect(body('flow-banner')).toBeNull();
    w.unmount();
  });

  it('saving a new version says which version it became', async () => {
    const f = flow('a', 12, 1, { used: true, currentVersionUsed: true, version: 2 });
    const w = await mountDialog({ flow: f });
    api.updateApprovalFlow.mockResolvedValue({ version: 3 } as never);
    await click('flow-save');
    expect(api.updateApprovalFlow).toHaveBeenCalledWith('a', expect.objectContaining({ key: 'a', toOptionId: 12, priority: 1 }));
    expect(notify.mock.calls.map((c) => (c[0] as { message: string }).message).join('|')).toContain('已儲存（第 3 版）');
    w.unmount();
  });

  it('fallback inherit shows the module default with names; custom offers users, group and manager only', async () => {
    const w = await mountDialog();
    expect(text('flow-fallback-mode')).toContain('王小明');
    expect(text('flow-fallback-mode')).not.toContain('找不到');
    await w.findComponent(quasar.QOptionGroup).vm.$emit('update:modelValue', 'custom');
    await flushPromises();
    const kind = w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === '備援來源')!;
    expect((kind.props('options') as { value: string }[]).map((o) => o.value)).toEqual(['users', 'group', 'manager']);
    w.unmount();
  });

  it('fallback default shows raw items, not missing, while names load', async () => {
    const w = await mountDialog({ users: 'pending' });
    expect(text('flow-fallback-mode')).toContain('user:u1');
    expect(text('flow-fallback-mode')).not.toContain('找不到');
    w.unmount();
  });

  it('field errors land on their inputs; constraint errors list at the top', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1) });
    api.updateApprovalFlow.mockRejectedValue({ status: 400, errors: { 'definition.rejectTo': ['退回狀態無效'], 'transitions[11>12]': ['入口沒有啟用的流程'] } });
    await click('flow-save');
    expect(document.body.textContent).toContain('退回狀態無效');
    expect(text('flow-top-errors')).toContain('入口沒有啟用的流程');
    expect(text('flow-top-errors')).not.toContain('退回狀態無效');
    w.unmount();
  });

  it('a stage error switches to the stages section and shows on that card', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1) });
    api.updateApprovalFlow.mockRejectedValue({ status: 400, errors: { 'definition.stages[0].approvers': ['找不到人'] } });
    await click('flow-save');
    expect(text('stage-card-0')).toContain('找不到人');
    expect(text('flow-top-errors')).toBe('');
    w.unmount();
  });

  it('a duplicate key (409) shows on the key input', async () => {
    const w = await mountDialog();
    await fillValid(w);
    api.createApprovalFlow.mockRejectedValue({ status: 409, message: '流程代碼已存在' });
    await click('flow-save');
    expect(document.body.textContent).toContain('流程代碼已存在');
    expect(notify).not.toHaveBeenCalled();
    w.unmount();
  });

  it('any edit clears the server errors', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1) });
    api.updateApprovalFlow.mockRejectedValue({ status: 400, errors: { 'definition.rejectTo': ['退回狀態無效'] } });
    await click('flow-save');
    expect(document.body.textContent).toContain('退回狀態無效');
    select(w, '退回後的狀態').vm.$emit('update:modelValue', 13);
    await flushPromises();
    expect(document.body.textContent).not.toContain('退回狀態無效');
    w.unmount();
  });

  it('changing target on edit moves priority to the end of the new target', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1), flows: [flow('a', 12, 1), flow('c', 13, 7)] });
    select(w, '目標狀態').vm.$emit('update:modelValue', 13);
    await flushPromises();
    await click('flow-save');
    expect(api.updateApprovalFlow.mock.calls[0]![1]).toMatchObject({ toOptionId: 13, priority: 8 });
    w.unmount();
  });

  it('mobile width opens maximized with top section tabs; desktop shows side tabs', async () => {
    const d = await mountDialog();
    expect(d.findComponent(quasar.QDialog).props('maximized')).toBe(false);
    expect(d.findComponent(quasar.QTabs).props('vertical')).toBe(true);
    d.unmount();
    window.innerWidth = 390;
    window.dispatchEvent(new Event('resize'));
    const m = await mountDialog();
    expect(m.findComponent(quasar.QDialog).props('maximized')).toBe(true);
    expect(m.findComponent(quasar.QTabs).props('vertical')).toBe(false);
    m.unmount();
  });
  // Shaped like what the backend returns: single-condition root, null parameters, a group fallback, a single-person stage saved as mode all.
  const backendShaped = (): ApprovalFlow => flow('a', 12, 3, {
    used: true, currentVersionUsed: true, version: 4, pendingCount: 2,
    definition: {
      shape: 'sequence', when: { field: 7, op: 'gte', value: 100 } as never, rejectTo: 13, fallbackApprovers: { source: 'group', groupIds: [3], userIds: null, level: null },
      stages: [
        { code: 'stage-1', name: { 'zh-TW': '主管', 'en-US': 'Manager' }, when: null, approvers: { source: 'manager', level: 1, userIds: null, groupIds: null, fieldId: null, candidateGroupIds: null, min: null, max: null, unmatchedGroups: null, candidatesUnmatched: false }, mode: 'all', editableFields: [7], dueHours: 24, onEmpty: 'skip' },
        { code: 'stage-3', name: { 'zh-TW': '會計' }, when: { all: [{ field: 7, op: 'gt', value: 5 }] } as never, approvers: { source: 'group', groupIds: [3] }, mode: 'all', editableFields: [], dueHours: null, onEmpty: 'fallback' },
      ],
    },
  });

  it('edit without changes sends the identical definition', async () => {
    const f = backendShaped();
    const w = await mountDialog({ flow: f });
    await click('flow-save');
    expect(api.updateApprovalFlow).toHaveBeenCalledTimes(1);
    expect(api.updateApprovalFlow.mock.calls[0]![1]).toEqual({ key: 'a', name: f.name, toOptionId: 12, priority: 3, active: true, definition: f.definition });
    w.unmount();
  });

  it('constraint-only errors stay on the basics section', async () => {
    const w = await mountDialog({ flow: flow('a', 12, 1) });
    api.updateApprovalFlow.mockRejectedValue({ status: 400, errors: { 'flows[a]': ['入口至少要有一個啟用的流程'] } });
    await click('flow-save');
    expect((body('flow-basic') as HTMLElement).style.display).not.toBe('none');
    expect((body('flow-stages-section') as HTMLElement).style.display).toBe('none');
    expect(text('flow-top-errors')).toContain('入口至少要有一個啟用的流程');
    w.unmount();
  });

  it('switching the fallback back to custom restores the saved custom value', async () => {
    const f = backendShaped();
    const w = await mountDialog({ flow: f });
    const group = w.findComponent(quasar.QOptionGroup);
    group.vm.$emit('update:modelValue', 'inherit');
    await flushPromises();
    group.vm.$emit('update:modelValue', 'custom');
    await flushPromises();
    await click('flow-save');
    expect(api.updateApprovalFlow.mock.calls[0]![1].definition.fallbackApprovers).toEqual(f.definition.fallbackApprovers);
    w.unmount();
  });
});
