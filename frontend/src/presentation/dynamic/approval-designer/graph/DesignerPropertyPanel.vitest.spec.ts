import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { DesignerField, ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ApprovalStageFields from '../ApprovalStageFields.vue';
import DesignerPropertyPanel from './DesignerPropertyPanel.vue';
import type { Selection } from './designer-graph';
import { useGraphDesigner } from './use-graph-designer';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), getGroups: vi.fn(), getEffectivePolicies: vi.fn(), getModulePolicies: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const status: DesignerField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
  options: [11, 12].map((n) => ({ id: n, value: `o${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: true })),
};
const amount: DesignerField = { ...status, id: 7, key: 'f_7', type: 'decimal', label: { 'zh-TW': '總額' }, options: [] };
const mod: ModuleDetail = { id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1, blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [status, amount] }] };
const wf: WorkflowResponse = { statusFieldId: 5, initialOptionId: 11, states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }], transitions: [], editableFieldIds: [7] };
const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 1 }, mode: 'all', editableFields: [], onEmpty: 'fallback' }, { id: 'n', kind: 'notify' }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'a', label: { 'zh-TW': '送出' } }, { id: 'e2', from: 'a', to: 'e' }],
});
const flow = (p: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 12, priority: 1, active: true, used: false, pendingCount: 0, moduleKey: 'doc', version: 1, versionId: 3, definition: def(), graph: def(), ...p,
});

function mountPanel(selection: Selection, opts: { canEdit?: boolean; flow?: ApprovalFlow | null } = {}) {
  api.getUsers.mockResolvedValue([]);
  api.getGroups.mockResolvedValue([]);
  api.getEffectivePolicies.mockResolvedValue({ values: {} } as never);
  api.getModulePolicies.mockResolvedValue([]);
  const designer = useGraphDesigner(() => opts.canEdit ?? true);
  if (opts.flow === null) designer.load(null, 12); else designer.load(opts.flow ?? flow());
  designer.select(selection);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  // 面板讀 designer.selection；用元件把 selection 和 designer 接起來，方便從外面改選取
  const Host = defineComponent({ setup: () => () => h(DesignerPropertyPanel, { designer, selection: designer.selection.value, module: mod, workflow: wf, closable: false }) });
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [createPinia(), [quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  return { w, designer };
}
const nodeOf = (d: ReturnType<typeof useGraphDesigner>, id: string) => d.state.value!.definition.nodes.find((n) => n.id === id)!;

beforeEach(() => { document.body.innerHTML = ''; vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });

describe('DesignerPropertyPanel', () => {
  it('nothing selected shows the flow properties', async () => {
    const { w } = mountPanel({ kind: 'flow' });
    await flushPromises();
    expect(w.find('[data-testid="flow-props-panel"]').exists()).toBe(true);
    expect(w.get('[data-testid="de-panel-title"]').text()).toBe('流程屬性');
    w.unmount();
  });

  it('name and target show the effective-now hint', async () => {
    const { w } = mountPanel({ kind: 'flow' });
    await flushPromises();
    expect(w.get('[data-testid="flow-immediate-name"]').text()).toBe('立即生效，不等發布');
    expect(w.get('[data-testid="flow-immediate-target"]').text()).toBe('立即生效，不等發布');
    w.unmount();
  });

  it('the flow key is editable only for a new flow; the target of a used flow is locked', async () => {
    const existing = mountPanel({ kind: 'flow' }, { flow: flow({ used: true }) });
    await flushPromises();
    expect((existing.w.get('[data-testid="flow-key"]').element as HTMLInputElement).readOnly).toBe(true);
    expect(existing.w.findComponent({ name: 'FlowBasicFields' }).props('targetLocked')).toBe(true);
    existing.w.unmount();
    const fresh = mountPanel({ kind: 'flow' }, { flow: null });
    await flushPromises();
    expect((fresh.w.get('[data-testid="flow-key"]').element as HTMLInputElement).readOnly).toBe(false);
    fresh.w.unmount();
  });

  it('editing the flow name and the start condition goes through the history', async () => {
    const { w, designer } = mountPanel({ kind: 'flow' });
    await flushPromises();
    const fields = w.findComponent({ name: 'FlowBasicFields' });
    fields.vm.$emit('patch', { name: { 'zh-TW': '大額採購' } });
    fields.vm.$emit('patch', { when: { all: [{ field: 7, op: 'gte', value: 100 }] } });
    expect(designer.meta.value!.name).toEqual({ 'zh-TW': '大額採購' });
    expect(designer.state.value!.definition.when).toBeTruthy();
    expect(designer.dirty.value).toBe(true);
    designer.undo(); designer.undo();
    expect(designer.dirty.value).toBe(false);
    w.unmount();
  });

  it('editing an approval node updates the working copy and is one undo step', async () => {
    const { w, designer } = mountPanel({ kind: 'node', id: 'a' });
    await flushPromises();
    const fields = w.findComponent(ApprovalStageFields);
    fields.vm.$emit('patch', { name: { 'zh-TW': '財' } });
    fields.vm.$emit('patch', { name: { 'zh-TW': '財務部' } });
    expect(nodeOf(designer, 'a').name).toEqual({ 'zh-TW': '財務部' });
    designer.undo();
    expect(nodeOf(designer, 'a').name).toEqual({ 'zh-TW': '財務' });
    expect(designer.undoable.value).toBe(false);
    w.unmount();
  });

  it('a single-person source forces any, and the chosen mode comes back with a group (as in the stage editor)', async () => {
    const { w, designer } = mountPanel({ kind: 'node', id: 'a' });
    await flushPromises();
    const fields = () => w.findComponent(ApprovalStageFields);
    fields().vm.$emit('patch', { approvers: { source: 'users', userIds: ['u1'] }, mode: 'any', chosenMode: 'all' });
    await flushPromises();
    expect(nodeOf(designer, 'a')).toMatchObject({ mode: 'any' });
    expect(nodeOf(designer, 'a')).not.toHaveProperty('chosenMode');
    expect(fields().props('modelValue').chosenMode).toBe('all');
    w.unmount();
  });

  it('editable field options come from the workflow', async () => {
    const { w } = mountPanel({ kind: 'node', id: 'a' });
    await flushPromises();
    expect(w.findComponent(ApprovalStageFields).props('editableFieldIds')).toEqual([7]);
    w.unmount();
  });

  it('the approval panel points to the condition node for field-dependent routing', async () => {
    const { w } = mountPanel({ kind: 'node', id: 'a' });
    await flushPromises();
    expect(w.get('[data-testid="approval-node-panel"]').text()).toContain('要依欄位走不同的人，請用條件節點');
    w.unmount();
  });

  it('renaming a published node code warns it becomes a new node; a duplicate code is refused', async () => {
    const { w, designer } = mountPanel({ kind: 'node', id: 'a' });
    await flushPromises();
    await w.get('.q-expansion-item__container .q-item').trigger('click');
    const input = w.get('[data-testid="node-code"]');
    const type = async (v: string) => { const el = w.get('[data-testid="node-code"]').element as HTMLInputElement; el.value = v; el.dispatchEvent(new Event('input')); await flushPromises(); };
    await type('approval-9');
    expect(w.find('[data-testid="node-code-warning"]').text()).toContain('會被當成新節點');
    await input.trigger('change');
    expect(designer.state.value!.definition.nodes.map((n) => n.id)).toContain('approval-9');
    expect(designer.selection.value).toEqual({ kind: 'node', id: 'approval-9' });
    await type('e');
    await w.get('[data-testid="node-code"]').trigger('change');
    expect(designer.state.value!.definition.nodes.map((n) => n.id)).toContain('approval-9');
    w.unmount();
  });

  it('a notify node only says it is not open yet', async () => {
    const { w } = mountPanel({ kind: 'node', id: 'n' });
    await flushPromises();
    expect(w.get('[data-testid="notify-panel"]').text()).toBe('通知節點尚未開放');
    w.unmount();
  });

  it('an edge shows its label field', async () => {
    const { w, designer } = mountPanel({ kind: 'edge', id: 'e1' });
    await flushPromises();
    expect(w.get('[data-testid="edge-panel"]').text()).toContain('線的標籤');
    expect((w.get('[data-testid="edge-panel"] input').element as HTMLInputElement).value).toBe('送出');
    expect(designer.state.value).toBeTruthy();
    w.unmount();
  });

  it('read-only panels are disabled', async () => {
    const { w } = mountPanel({ kind: 'node', id: 'a' }, { canEdit: false });
    await flushPromises();
    expect(w.findComponent(ApprovalStageFields).props('disable')).toBe(true);
    const flowPanel = mountPanel({ kind: 'flow' }, { canEdit: false });
    await flushPromises();
    expect(flowPanel.w.findComponent({ name: 'FlowBasicFields' }).props('disable')).toBe(true);
    w.unmount();
    flowPanel.w.unmount();
  });

  it('backend errors on a node show on its fields and as a node-level message', async () => {
    const { w, designer } = mountPanel({ kind: 'node', id: 'a' });
    designer.issues.value = { errors: { nodes: { a: [{ key: 'definition.nodes.a.approvers', field: 'approvers', message: '請選簽核人' }, { key: 'definition.nodes.a', field: null, message: '這個節點不能到達' }] }, edges: {}, flow: [] }, warnings: { nodes: {}, edges: {}, flow: [] } };
    await flushPromises();
    expect(w.findComponent(ApprovalStageFields).props('errors')).toMatchObject({ approvers: '請選簽核人', _: '這個節點不能到達' });
    expect(w.text()).toContain('這個節點不能到達');
    w.unmount();
  });
});
