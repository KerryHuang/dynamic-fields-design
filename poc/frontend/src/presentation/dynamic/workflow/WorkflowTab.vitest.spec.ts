import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, ModuleDetail, Workflow, WorkflowResponse } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import WorkflowTab from './WorkflowTab.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getWorkflow: vi.fn(), putWorkflow: vi.fn(), deleteWorkflow: vi.fn(), getGroups: vi.fn(), getMe: vi.fn(), getApprovalFlows: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const status: DesignerField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
  options: [11, 12, 13].map((n) => ({ id: n, value: `opt_${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: n !== 13 })),
};
const mod = (extra: Partial<ModuleDetail> = {}): ModuleDetail => ({
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [status] }], ...extra,
});
const saved: Workflow = {
  statusFieldId: 5, initialOptionId: 11,
  states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: true }],
  transitions: [{ from: 11, to: 12, groupIds: [3] }],
};

const flowOn = (toOptionId: number, key = `f${toOptionId}`) => ({ key, toOptionId }) as never;

async function mountTab(m: ModuleDetail, existing: Workflow | WorkflowResponse | null, flows: unknown[] | null = []) {
  api.getWorkflow.mockImplementation(async () => {
    if (existing) return existing;
    throw { status: 404 };
  });
  if (flows) api.getApprovalFlows.mockResolvedValue(flows as never);
  else api.getApprovalFlows.mockRejectedValue({ status: 500 });
  api.getGroups.mockResolvedValue([{ id: 3, key: 'mgr', name: { 'zh-TW': '主管' }, members: [] }]);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: true, delete: true } } as never);
  const qc = new QueryClient();
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(WorkflowTab, {
    props: { module: m },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  await flushPromises();
  return Object.assign(w, { qc });
}

describe('WorkflowTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('loads a saved workflow into the form and saves it back unchanged', async () => {
    api.putWorkflow.mockResolvedValue(saved);
    const w = await mountTab(mod(), saved);
    expect((w.find('[data-testid="state-included-11"]').element as HTMLElement).getAttribute('aria-checked')).toBe('true');
    expect(w.find('[data-testid="state-row-13"]').exists()).toBe(false); // inactive options are not offered
    await w.find('[data-testid="save-workflow"]').trigger('click');
    await flushPromises();
    expect(api.putWorkflow).toHaveBeenCalledWith('doc', saved);
  });

  it('save round-trips approval and editable', async () => {
    const withApproval: Workflow = {
      ...saved,
      states: [{ optionId: 11, readOnly: false, editable: true }, { optionId: 12, readOnly: true }],
      transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'block' } }],
    };
    api.putWorkflow.mockResolvedValue(withApproval);
    const w = await mountTab(mod(), withApproval, [flowOn(12)]);
    await w.find('[data-testid="save-workflow"]').trigger('click');
    await flushPromises();
    expect(api.putWorkflow).toHaveBeenCalledWith('doc', withApproval);
  });

  it('shows read-only approval and editable marks; edits to other parts keep them on save', async () => {
    const withApproval: WorkflowResponse = {
      ...saved, lockedStates: [12], editableStates: [11], lockedFieldIds: [],
      states: [{ optionId: 11, readOnly: false, editable: true }, { optionId: 12, readOnly: true }],
      transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'direct' } }],
    };
    api.putWorkflow.mockResolvedValue(withApproval);
    const w = await mountTab(mod(), withApproval, [flowOn(12)]);
    expect(w.find('[data-testid="transition-approval-toggle-0"]').attributes('aria-checked')).toBe('true');
    expect(w.find('[data-testid="state-editable-11"]').text()).toBe('可編輯');
    expect(w.find('[data-testid="state-editable-12"]').exists()).toBe(false);
    w.findAllComponents({ name: 'QSelect' })[3]!.vm.$emit('update:modelValue', []);
    await w.find('[data-testid="state-readonly-11"]').trigger('click');
    await flushPromises();
    await w.find('[data-testid="save-workflow"]').trigger('click');
    await flushPromises();
    const sent = api.putWorkflow.mock.calls[0]![1] as Workflow;
    expect(sent.transitions[0]).toEqual({ from: 11, to: 12, groupIds: [], approval: { required: true, noMatch: 'direct' } });
    expect(sent.states[0]).toEqual({ optionId: 11, readOnly: true, editable: true });
  });

  it('removing an approval transition asks first; a plain one is removed at once', async () => {
    const mixed: Workflow = {
      ...saved,
      transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'block' } }, { from: 12, to: 11, groupIds: [] }],
    };
    const w = await mountTab(mod(), mixed);
    await w.find('[data-testid="transition-remove-1"]').trigger('click');
    expect(w.find('[data-testid="transition-row-1"]').exists()).toBe(false);
    await w.find('[data-testid="transition-remove-0"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="transition-row-0"]').exists()).toBe(true);
    expect(document.body.textContent).toContain('這條轉移需要簽核');
    (document.body.querySelector('[data-testid="confirm-remove-transition"]') as HTMLElement).click();
    await flushPromises();
    expect(w.find('[data-testid="transition-row-0"]').exists()).toBe(false);
    w.unmount();
  });

  it('cannot be saved until a status field and an initial state are picked', async () => {
    const w = await mountTab(mod(), null);
    expect(w.find('[data-testid="save-workflow"]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-testid="remove-workflow"]').exists()).toBe(false);
  });

  it('shows the server messages when saving fails', async () => {
    api.putWorkflow.mockRejectedValue({ status: 400, errors: { transitions: ['轉移的狀態不在狀態清單內'] } });
    const w = await mountTab(mod(), saved);
    await w.find('[data-testid="save-workflow"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="workflow-errors"]').text()).toContain('轉移的狀態不在狀態清單內');
  });

  it('removes the workflow after confirming', async () => {
    api.deleteWorkflow.mockResolvedValue(undefined as never);
    const w = await mountTab(mod(), saved);
    await w.find('[data-testid="remove-workflow"]').trigger('click');
    await flushPromises();
    (document.body.querySelector('[data-testid="confirm-remove-workflow"]') as HTMLElement).click();
    await flushPromises();
    expect(api.deleteWorkflow).toHaveBeenCalledWith('doc');
    w.unmount();
  });

  it('explains that detail modules cannot have a workflow', async () => {
    const w = await mountTab(mod({ detailOf: 'header' }), null);
    expect(w.find('[data-testid="workflow-unsupported"]').exists()).toBe(true);
    expect(w.find('[data-testid="save-workflow"]').exists()).toBe(false);
  });

  describe('approval on transitions', () => {
    const toggle = (w: Awaited<ReturnType<typeof mountTab>>) => w.find('[data-testid="transition-approval-toggle-0"]');
    const lastBody = () => api.putWorkflow.mock.calls.at(-1)![1] as Workflow;

    it('approval toggle adds required approval with block and shows the noMatch select', async () => {
      api.putWorkflow.mockResolvedValue(saved as never);
      const w = await mountTab(mod(), saved, [flowOn(12)]);
      expect(w.find('[data-testid="transition-nomatch-0"]').exists()).toBe(false);
      await toggle(w).trigger('click');
      expect(w.find('[data-testid="transition-nomatch-0"]').exists()).toBe(true);
      await w.find('[data-testid="save-workflow"]').trigger('click');
      await flushPromises();
      expect(lastBody().transitions[0]!.approval).toEqual({ required: true, noMatch: 'block' });
    });

    it('turning approval off sends no approval for that transition', async () => {
      const withApproval: Workflow = { ...saved, transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'block' } }] };
      api.putWorkflow.mockResolvedValue(withApproval as never);
      const w = await mountTab(mod(), withApproval, [flowOn(12)]);
      await toggle(w).trigger('click');
      await w.find('[data-testid="save-workflow"]').trigger('click');
      await flushPromises();
      expect('approval' in lastBody().transitions[0]!).toBe(false);
    });

    it('direct noMatch shows the direct warning text', async () => {
      const withApproval: Workflow = { ...saved, transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'block' } }] };
      const w = await mountTab(mod(), withApproval, [flowOn(12)]);
      expect(w.find('[data-testid="transition-direct-warning-0"]').exists()).toBe(false);
      w.findAllComponents({ name: 'QSelect' })[4]!.vm.$emit('update:modelValue', 'direct');
      await flushPromises();
      expect(w.find('[data-testid="transition-direct-warning-0"]').text()).toBe('條件都不符合的單據會直接轉移，不經任何人同意');
    });

    it('flow count link shows N flows on the target and emits open-flows with the target option', async () => {
      const withApproval: Workflow = { ...saved, transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'block' } }] };
      const w = await mountTab(mod(), withApproval, [flowOn(12, 'a'), flowOn(12, 'b'), flowOn(11, 'c')]);
      const link = w.find('[data-testid="transition-flows-0"]');
      expect(link.text()).toContain('目標狀態已掛 2 個流程');
      await link.trigger('click');
      expect(w.emitted('open-flows')).toEqual([[12]]);
    });

    it('save disabled while an approval transition targets a state with no flow, with the reason', async () => {
      const w = await mountTab(mod(), saved, [flowOn(11)]);
      await toggle(w).trigger('click');
      expect(w.find('[data-testid="save-workflow"]').attributes('disabled')).toBeDefined();
      expect(w.find('[data-testid="workflow-no-flow"]').text()).toContain('「狀態12」還沒有簽核流程');
    });

    it('does not block saving while the flow list has not loaded', async () => {
      const w = await mountTab(mod(), saved, null);
      await toggle(w).trigger('click');
      expect(w.find('[data-testid="save-workflow"]').attributes('disabled')).toBeUndefined();
    });

    it('unsaved draft survives a workflow refetch', async () => {
      const w = await mountTab(mod(), saved, [flowOn(12)]);
      await toggle(w).trigger('click');
      const fresh: WorkflowResponse = { ...saved, lockedStates: [12], editableStates: [], lockedFieldIds: [7] };
      w.qc.setQueryData(['workflow', 'doc'], fresh);
      await flushPromises();
      expect(toggle(w).attributes('aria-checked')).toBe('true');
      api.putWorkflow.mockResolvedValue(saved as never);
      await w.find('[data-testid="save-workflow"]').trigger('click');
      await flushPromises();
      expect(lastBody().transitions[0]!.approval).toEqual({ required: true, noMatch: 'block' });
    });

    it('a refetch replaces the draft when nothing is unsaved', async () => {
      const w = await mountTab(mod(), saved, [flowOn(12)]);
      const fresh: WorkflowResponse = { ...saved, transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'block' } }], lockedStates: [12], editableStates: [], lockedFieldIds: [] };
      w.qc.setQueryData(['workflow', 'doc'], fresh);
      await flushPromises();
      expect(toggle(w).attributes('aria-checked')).toBe('true');
    });

    it('saving resets the draft from the server response', async () => {
      const w = await mountTab(mod(), saved, [flowOn(12)]);
      await toggle(w).trigger('click');
      const response: WorkflowResponse = {
        ...saved, transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'direct' } }],
        lockedStates: [12], editableStates: [], lockedFieldIds: [],
      };
      api.putWorkflow.mockResolvedValue(response);
      await w.find('[data-testid="save-workflow"]').trigger('click');
      await flushPromises();
      expect(w.find('[data-testid="transition-direct-warning-0"]').exists()).toBe(true);
    });

    it('read-only designer sees the toggle disabled', async () => {
      const w = await mountTab(mod(), saved, [flowOn(12)]);
      api.getMe.mockResolvedValue({ designer: { read: true, add: false, edit: false, delete: false } } as never);
      await w.qc.invalidateQueries();
      await flushPromises();
      expect(toggle(w).attributes('aria-disabled')).toBe('true');
    });
  });

  describe('lock marks, editable states and row errors', () => {
    const response = (extra: Partial<WorkflowResponse> = {}): WorkflowResponse => ({
      ...saved, states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: true }, { optionId: 13, readOnly: false }],
      lockedStates: [], editableStates: [], lockedFieldIds: [], ...extra,
    });
    const lastBody = () => api.putWorkflow.mock.calls.at(-1)![1] as Workflow;
    type Wrapper = Awaited<ReturnType<typeof mountTab>>;
    const click = async (w: Wrapper, id: string) => { await w.find(`[data-testid="${id}"]`).trigger('click'); await flushPromises(); };
    const saveClick = (w: Wrapper) => click(w, 'save-workflow');

    it('initial state editable toggle is on and disabled', async () => {
      const w = await mountTab(mod(), response());
      const t = w.find('[data-testid="state-editable-toggle-11"]');
      expect(t.attributes('aria-checked')).toBe('true');
      expect(t.attributes('aria-disabled')).toBe('true');
      expect(w.find('[data-testid="state-editable-reason-11"]').text()).toBe('起始狀態固定可編輯');
    });

    it('approval target editable toggle is disabled with the lock reason', async () => {
      const w = await mountTab(mod(), response(), [flowOn(12)]);
      await click(w, 'state-editable-toggle-12');
      expect(w.find('[data-testid="state-editable-toggle-12"]').attributes('aria-checked')).toBe('true');
      await click(w, 'transition-approval-toggle-0');
      const t = w.find('[data-testid="state-editable-toggle-12"]');
      expect(t.attributes('aria-checked')).toBe('false');
      expect(t.attributes('aria-disabled')).toBe('true');
      expect(w.find('[data-testid="state-editable-reason-12"]').text()).toBe('需簽核轉移的目標狀態以鎖定為準');
      api.putWorkflow.mockResolvedValue(response() as never);
      await saveClick(w);
      expect(lastBody().states[1]).toEqual({ optionId: 12, readOnly: true });
    });

    it('editable toggle round-trips in the save body', async () => {
      api.putWorkflow.mockResolvedValue(response() as never);
      const w = await mountTab(mod(), response());
      await click(w, 'state-editable-toggle-12');
      await saveClick(w);
      expect(lastBody().states.find((x) => x.optionId === 12)).toEqual({ optionId: 12, readOnly: true, editable: true });
    });

    it('locked states show the lock badge from the saved workflow', async () => {
      const w = await mountTab(mod(), response({ lockedStates: [12] }));
      expect(w.find('[data-testid="state-locked-12"]').text()).toContain('簽核鎖定');
      expect(w.find('[data-testid="state-locked-11"]').exists()).toBe(false);
    });

    it('locked field names are listed under the states', async () => {
      const f = { ...status, id: 9, key: 'f_9', type: 'text', label: { 'zh-TW': '附件說明' }, options: [] } as DesignerField;
      const m = mod();
      m.blocks[0]!.fields = [status, f];
      const w = await mountTab(m, response({ lockedFieldIds: [9] }));
      expect(w.find('[data-testid="locked-fields"]').text()).toBe('簽核鎖定狀態中不能修改的欄位：附件說明（附件欄位一律鎖定）');
      const none = await mountTab(mod(), response());
      expect(none.find('[data-testid="locked-fields"]').exists()).toBe(false);
    });

    it('dirty draft shows the recompute-after-save caption', async () => {
      const w = await mountTab(mod(), response(), [flowOn(12)]);
      expect(w.find('[data-testid="lock-dirty-caption"]').exists()).toBe(false);
      await click(w, 'transition-approval-toggle-0');
      expect(w.find('[data-testid="lock-dirty-caption"]').text()).toContain('儲存後重新計算');
    });

    it('transition error key lands on that transition row', async () => {
      api.putWorkflow.mockRejectedValue({ status: 400, errors: { 'transitions[11>12]': ['這條轉移有問題'] } });
      const w = await mountTab(mod(), response());
      await saveClick(w);
      expect(w.find('[data-testid="transition-error-0"]').text()).toBe('這條轉移有問題');
      expect(w.find('[data-testid="workflow-errors"]').exists()).toBe(false);
    });

    it('state error key lands on that state row', async () => {
      api.putWorkflow.mockRejectedValue({ status: 400, errors: { 'states[12]': ['這個狀態有問題'], initialOptionId: ['起始狀態不能鎖定'] } });
      const w = await mountTab(mod(), response());
      await saveClick(w);
      expect(w.find('[data-testid="state-error-12"]').text()).toBe('這個狀態有問題');
      expect(w.find('[data-testid="initial-error"]').text()).toBe('起始狀態不能鎖定');
    });

    it('flow error lists the flow name', async () => {
      api.putWorkflow.mockRejectedValue({ status: 400, errors: { 'flows[po-review]': ['關卡條件引用了鎖定欄位'], 'events[ship]': ['事件會推進狀態'] } });
      const w = await mountTab(mod(), response(), [{ key: 'po-review', toOptionId: 12, name: { 'zh-TW': '採購簽核' } }]);
      await saveClick(w);
      const text = w.find('[data-testid="workflow-errors"]').text();
      expect(text).toContain('採購簽核：關卡條件引用了鎖定欄位');
      expect(text).toContain('事件會推進狀態');
    });

    it('errors clear on the next edit', async () => {
      api.putWorkflow.mockRejectedValue({ status: 400, errors: { 'transitions[11>12]': ['x'], 'events[a]': ['y'] } });
      const w = await mountTab(mod(), response());
      await saveClick(w);
      await click(w, 'state-readonly-11');
      expect(w.find('[data-testid="transition-error-0"]').exists()).toBe(false);
      expect(w.find('[data-testid="workflow-errors"]').exists()).toBe(false);
    });

    it('409 from the pending guard is shown in the error list', async () => {
      api.putWorkflow.mockRejectedValue({ status: 409, message: '有 2 張進行中的簽核單，不能改這條轉移' });
      const w = await mountTab(mod(), response());
      await saveClick(w);
      expect(w.find('[data-testid="workflow-errors"]').text()).toContain('有 2 張進行中的簽核單');
    });
  });

  describe('phone width', () => {
    // Quasar's Screen singleton follows window resizes; make it react at once and put it back afterwards.
    const resizeTo = async (width: number) => {
      Object.defineProperty(window, 'innerWidth', { value: width, configurable: true });
      window.dispatchEvent(new Event('resize'));
      await flushPromises();
    };
    const withWidth = async (width: number, fn: () => Promise<void>) => {
      const old = window.innerWidth;
      quasar.Screen.setDebounce(0);
      await resizeTo(width);
      try { await fn(); } finally { await resizeTo(old); quasar.Screen.setDebounce(50); }
    };
    const approved: Workflow = { ...saved, transitions: [{ from: 11, to: 12, groupIds: [3], approval: { required: true, noMatch: 'direct' } }] };

    it('mobile width renders state cards without a table', async () => {
      await withWidth(390, async () => {
        const w = await mountTab(mod(), saved);
        expect(w.find('.q-table').exists()).toBe(false);
        expect(w.find('[data-testid="state-cards"]').exists()).toBe(true);
        expect(w.find('[data-testid="state-row-11"]').classes()).toContain('q-card');
        for (const id of ['included', 'initial', 'readonly', 'editable-toggle']) expect(w.find(`[data-testid="state-${id}-11"]`).exists()).toBe(true);
        expect(w.find('[data-testid="state-row-11"]').text()).toContain('可編輯（退回重送）');
        // the toggles still edit the draft
        await w.find('[data-testid="state-readonly-11"]').trigger('click');
        expect(w.find('[data-testid="state-readonly-11"]').attributes('aria-checked')).toBe('true');
      });
    });

    it('mobile width stacks transition fields in a card', async () => {
      await withWidth(390, async () => {
        const w = await mountTab(mod(), approved, [flowOn(12)]);
        const row = w.find('[data-testid="transition-row-0"]');
        expect(row.classes()).toContain('q-card');
        const selects = row.findAll('.q-field');
        expect(selects.length).toBeGreaterThanOrEqual(4); // from, to, groups, noMatch
        for (const f of selects) expect(f.classes()).toContain('col-12');
        expect(row.find('[data-testid="transition-flows-0"]').exists()).toBe(true);
        expect(row.find('[data-testid="transition-remove-0"]').exists()).toBe(true);
      });
    });

    it('wide width keeps the table', async () => {
      const w = await mountTab(mod(), saved);
      expect(w.find('.q-table').exists()).toBe(true);
      expect(w.find('[data-testid="state-cards"]').exists()).toBe(false);
      expect(w.find('[data-testid="transition-row-0"]').classes()).not.toContain('q-card');
    });
  });
});
