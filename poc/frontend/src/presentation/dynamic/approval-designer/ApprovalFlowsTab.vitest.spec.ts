import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { DesignerField, ModuleDetail, Workflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ApprovalFlowsTab from './ApprovalFlowsTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getWorkflow: vi.fn(), getApprovalFlows: vi.fn(), getMe: vi.fn(), updateApprovalFlow: vi.fn(), deleteApprovalFlow: vi.fn(), reorderApprovalFlows: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const num: DesignerField = {
  id: 7, key: 'f_7', type: 'decimal', label: { 'zh-TW': '總額' }, blockId: 1, sequence: 2, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
};
const status: DesignerField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
  options: [11, 12, 13].map((n) => ({ id: n, value: `opt_${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: true })),
};
const mod = (extra: Partial<ModuleDetail> = {}): ModuleDetail => ({
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [status, num] }], ...extra,
});
const wf: Workflow = {
  statusFieldId: 5, initialOptionId: 11,
  states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }, { optionId: 13, readOnly: true }],
  transitions: [{ from: 11, to: 12, groupIds: [], approval: { required: true, noMatch: 'block' } }, { from: 12, to: 13, groupIds: [] }],
};
const flow = (key: string, toOptionId: number, priority: number, extra: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key, name: { 'zh-TW': `流程${key}` }, toOptionId, priority, active: true, moduleKey: 'doc', version: 2, used: false, pendingCount: 0,
  definition: { shape: 'sequence', when: { field: 7, op: 'gte', value: 100 } as never, stages: [{ code: 's1', name: { 'zh-TW': 'a' }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' }] },
  ...extra,
});

async function mountTab(flows: ApprovalFlow[], opts: { workflow?: Workflow | null; edit?: boolean; focusTarget?: number | null; m?: ModuleDetail } = {}) {
  api.getWorkflow.mockImplementation(async () => {
    if (opts.workflow === null) throw { status: 404 };
    return (opts.workflow ?? wf) as never;
  });
  api.getApprovalFlows.mockResolvedValue(flows);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: opts.edit ?? true, delete: true } } as never);
  api.updateApprovalFlow.mockResolvedValue({} as never);
  api.reorderApprovalFlows.mockResolvedValue([] as never);
  api.deleteApprovalFlow.mockResolvedValue(undefined as never);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(ApprovalFlowsTab, {
    attachTo: document.body,
    props: { module: opts.m ?? mod(), focusTarget: opts.focusTarget },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n,
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  notify = vi.fn();
  (w.vm as unknown as { $q: { notify: unknown } }).$q.notify = notify;
  await flushPromises();
  return w;
}
// Notify does not render under jsdom, so $q.notify is replaced by a spy after mount.
let notify: ReturnType<typeof vi.fn>;
const notified = (text: string) => expect(notify.mock.calls.map((c) => (c[0] as { message: string }).message).join('|')).toContain(text);
const body = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('ApprovalFlowsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    quasar.Screen.setDebounce(0);
  });

  it('groups flows by target state with entry transitions in the header', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 13, 1)]);
    expect(w.find('[data-testid="flow-group-12"]').text()).toContain('狀態12');
    expect(w.find('[data-testid="flow-entries-12"]').text()).toContain('狀態11 → 狀態12');
    expect(w.findAll('[data-testid^="flow-group-"]').map((g) => g.attributes('data-testid'))).toEqual(['flow-group-12', 'flow-group-13']);
    w.unmount();
  });

  it('card shows name, condition summary, stage count, version and pending count', async () => {
    const w = await mountTab([flow('a', 12, 1, { pendingCount: 3 })]);
    const card = w.find('[data-testid="flow-card-a"]').text();
    expect(card).toContain('流程a');
    expect(card).toContain('總額');
    expect(card).toContain('1 關');
    expect(card).toContain('第 2 版');
    expect(card).toContain('進行中 3 張');
    w.unmount();
  });

  it('target without approval entries shows the no-entry warning', async () => {
    const w = await mountTab([flow('b', 13, 1)]);
    expect(w.find('[data-testid="flow-no-entry-13"]').text()).toBe('這個狀態還沒有需簽核的入口，流程不會被用到');
    w.unmount();
  });

  it('move down sends the reordered keys for that target and notifies future-only', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 12, 2), flow('c', 13, 1)]);
    await w.find('[data-testid="flow-down-a"]').trigger('click');
    await flushPromises();
    expect(api.reorderApprovalFlows).toHaveBeenCalledWith('doc', 12, ['b', 'a']);
    notified('已調整優先序，只影響之後送出的單');
    w.unmount();
  });

  it('a group with a single flow has both move buttons disabled', async () => {
    const w = await mountTab([flow('a', 12, 1)]);
    expect(w.find('[data-testid="flow-up-a"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="flow-down-a"]').attributes('disabled')).toBeDefined();
    w.unmount();
  });

  it('deactivate asks for confirmation with the pending count, then saves active false with the same definition', async () => {
    const f = flow('a', 12, 1, { pendingCount: 4, used: true });
    const w = await mountTab([f]);
    await w.find('[data-testid="flow-active-a"]').trigger('click');
    await flushPromises();
    expect(api.updateApprovalFlow).not.toHaveBeenCalled();
    expect(body('flow-off-confirm-text')!.textContent).toContain('進行中的 4 張');
    body('flow-off-confirm')!.click();
    await flushPromises();
    expect(api.updateApprovalFlow).toHaveBeenCalledWith('a', { key: 'a', name: f.name, toOptionId: 12, priority: 1, active: false, definition: f.definition });
    w.unmount();
  });

  it('activate saves immediately, without a confirmation', async () => {
    const w = await mountTab([flow('a', 12, 1, { active: false })]);
    expect(w.find('[data-testid="flow-inactive-a"]').exists()).toBe(true);
    await w.find('[data-testid="flow-active-a"]').trigger('click');
    await flushPromises();
    expect(body('flow-off-confirm')).toBeNull();
    expect(api.updateApprovalFlow).toHaveBeenCalledWith('a', expect.objectContaining({ active: true }));
    notified('已啟用，只影響之後送出的單');
    w.unmount();
  });

  it('used flow has no delete action; unused flow delete confirms and calls delete', async () => {
    const w = await mountTab([flow('a', 12, 1, { used: true }), flow('b', 12, 2)]);
    expect(w.find('[data-testid="flow-delete-a"]').exists()).toBe(false);
    expect(w.find('[data-testid="flow-used-a"]').exists()).toBe(true);
    await w.find('[data-testid="flow-delete-b"]').trigger('click');
    expect(api.deleteApprovalFlow).not.toHaveBeenCalled();
    body('flow-delete-confirm')!.click();
    await flushPromises();
    expect(api.deleteApprovalFlow).toHaveBeenCalledWith('b');
    w.unmount();
  });

  it('backend 400 on deactivate shows the message', async () => {
    const w = await mountTab([flow('a', 12, 1)]);
    api.updateApprovalFlow.mockRejectedValue({ status: 400, errors: { active: ['直接轉移的入口至少要有一個啟用中的流程'] } });
    await w.find('[data-testid="flow-active-a"]').trigger('click');
    await flushPromises();
    body('flow-off-confirm')!.click();
    await flushPromises();
    notified('直接轉移的入口至少要有一個啟用中的流程');
    w.unmount();
  });

  it('desktop cards are draggable and a drop reorders; mobile cards are not draggable', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 12, 2)]);
    const a = w.find('[data-testid="flow-card-a"]');
    expect(a.attributes('draggable')).toBe('true');
    await a.trigger('dragstart');
    await w.find('[data-testid="flow-card-b"]').trigger('drop');
    await flushPromises();
    expect(api.reorderApprovalFlows).toHaveBeenCalledWith('doc', 12, ['b', 'a']);
    w.unmount();

    window.innerWidth = 390;
    window.dispatchEvent(new Event('resize'));
    const m = await mountTab([flow('a', 12, 1), flow('b', 12, 2)]);
    await flushPromises();
    expect(m.find('[data-testid="flow-card-a"]').attributes('draggable')).toBe('false');
    expect(m.find('[data-testid="flow-menu-a"]').exists()).toBe(true);
    expect(m.find('[data-testid="flow-up-a"]').exists()).toBe(false);
    m.unmount();
    window.innerWidth = 1024;
    window.dispatchEvent(new Event('resize'));
  });

  it('a second move while a reorder is pending sends nothing (desktop buttons, drop and mobile menu)', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 12, 2), flow('c', 12, 3)]);
    api.reorderApprovalFlows.mockReturnValue(new Promise(() => {}));
    await w.find('[data-testid="flow-down-a"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="flow-down-b"]').attributes('disabled')).toBeDefined();
    await w.find('[data-testid="flow-down-a"]').trigger('click');
    await w.find('[data-testid="flow-card-a"]').trigger('dragstart');
    await w.find('[data-testid="flow-card-c"]').trigger('drop');
    expect(api.reorderApprovalFlows).toHaveBeenCalledTimes(1);
    w.unmount();

    window.innerWidth = 390;
    window.dispatchEvent(new Event('resize'));
    const m = await mountTab([flow('a', 12, 1), flow('b', 12, 2)]);
    api.reorderApprovalFlows.mockClear();
    api.reorderApprovalFlows.mockReturnValue(new Promise(() => {}));
    await m.find('[data-testid="flow-menu-a"]').trigger('click');
    (body('flow-down-a') as HTMLElement).click();
    await flushPromises();
    await m.find('[data-testid="flow-menu-a"]').trigger('click');
    expect(body('flow-down-a')!.getAttribute('aria-disabled')).toBe('true');
    (body('flow-down-a') as HTMLElement).click();
    expect(api.reorderApprovalFlows).toHaveBeenCalledTimes(1);
    m.unmount();
    window.innerWidth = 1024;
    window.dispatchEvent(new Event('resize'));
  });

  it('a drop from another target group is ignored', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 13, 1)]);
    await w.find('[data-testid="flow-card-a"]').trigger('dragstart');
    await w.find('[data-testid="flow-card-b"]').trigger('drop');
    expect(api.reorderApprovalFlows).not.toHaveBeenCalled();
    w.unmount();
  });

  it('a reorder 400 shows the backend message', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 12, 2)]);
    api.reorderApprovalFlows.mockRejectedValue({ status: 400, errors: { keys: ['必須剛好列出這個目標狀態上的全部流程'] } });
    await w.find('[data-testid="flow-down-a"]').trigger('click');
    await flushPromises();
    notified('必須剛好列出這個目標狀態上的全部流程');
    w.unmount();
  });

  it('buttons carry the flow name in their aria-label', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 12, 2)]);
    expect(w.find('[data-testid="flow-down-a"]').attributes('aria-label')).toContain('流程a');
    expect(w.find('[data-testid="flow-delete-b"]').attributes('aria-label')).toContain('流程b');
    w.unmount();
  });

  it('deactivate confirm reads without the pending sentence when none are running', async () => {
    const w = await mountTab([flow('a', 12, 1)]);
    await w.find('[data-testid="flow-active-a"]').trigger('click');
    await flushPromises();
    expect(body('flow-off-confirm-text')!.textContent).toBe('停用後，之後送出的單不再比對這個流程。');
    w.unmount();
  });

  it('read-only designer sees no toggles or actions', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 12, 2)], { edit: false });
    expect(w.find('[data-testid="flow-card-a"]').exists()).toBe(true);
    expect(w.find('[data-testid="flow-active-a"]').exists()).toBe(false);
    expect(w.find('[data-testid="flow-down-a"]').exists()).toBe(false);
    expect(w.find('[data-testid="flow-delete-b"]').exists()).toBe(false);
    w.unmount();
  });

  it('module without workflow shows the create-workflow hint', async () => {
    const w = await mountTab([], { workflow: null });
    expect(w.find('[data-testid="flows-no-workflow"]').text()).toContain('先到「狀態機」分頁建立');
    w.unmount();
  });

  it('focusTarget on a state with no saved entry and no flow still shows and highlights its group', async () => {
    const w = await mountTab([flow('a', 12, 1)], { focusTarget: 13 });
    const g = w.find('[data-testid="flow-group-13"]');
    expect(g.exists()).toBe(true);
    expect(g.classes()).toContain('flow-group--focus');
    expect(g.text()).toContain('這個狀態還沒有流程');
    expect(w.find('[data-testid="flow-draft-13"]').exists()).toBe(true);
    w.unmount();
  });

  it('focusTarget highlights that group', async () => {
    const w = await mountTab([flow('a', 12, 1), flow('b', 13, 1)], { focusTarget: 13 });
    expect(w.find('[data-testid="flow-group-13"]').classes()).toContain('flow-group--focus');
    expect(w.find('[data-testid="flow-group-12"]').classes()).not.toContain('flow-group--focus');
    w.unmount();
  });
});
