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
    const withApproval: Workflow = {
      ...saved,
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
});
