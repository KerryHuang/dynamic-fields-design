import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter, RouterView } from 'vue-router';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { DesignerField, ModuleDetail, Workflow } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import FlowDesignerPage from './FlowDesignerPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getModule: vi.fn(), getWorkflow: vi.fn(), getApprovalFlow: vi.fn(), getApprovalFlows: vi.fn(), getMe: vi.fn(), getUsers: vi.fn(), getGroups: vi.fn(), getEffectivePolicies: vi.fn(), getModulePolicies: vi.fn(), validateApprovalFlow: vi.fn(), saveApprovalFlowDraft: vi.fn(), createApprovalFlowDraft: vi.fn(), discardApprovalFlowDraft: vi.fn(), deleteApprovalFlow: vi.fn(), publishApprovalFlow: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const canvasCalls = vi.hoisted(() => ({ fitAll: vi.fn(), focusNode: vi.fn() }));
// 真實的畫布只在瀏覽器驗；這裡的替身把收到的節點與線寫成文字，並提供觸發事件的按鈕。
const CanvasStub = defineComponent({
  name: 'DesignerCanvas', props: ['canvasId', 'nodes', 'edges', 'draggable', 'initialNodes'], emits: ['node-open', 'edge-open', 'pane-click', 'node-move'],
  setup(p, { emit, expose }) {
    expose(canvasCalls);
    return () => h('div', { 'data-testid': 'canvas', 'data-id': p.canvasId, 'data-draggable': String(p.draggable), 'data-initial': JSON.stringify(p.initialNodes ?? null) }, [
      ...p.nodes.map((n: { id: string; data: { label: string }; position: { x: number } }) => h('button', { 'data-testid': `n-${n.id}`, 'data-x': n.position.x, onClick: () => emit('node-open', n.id) }, n.data.label)),
      h('button', { 'data-testid': 'move-a', onClick: () => emit('node-move', 'a', { x: 500, y: 40 }) }),
      h('button', { 'data-testid': 'pane', onClick: () => emit('pane-click') }),
      h('button', { 'data-testid': 'edge-e1', onClick: () => emit('edge-open', 'e1') }),
    ]);
  },
});

const num: DesignerField = {
  id: 5, key: 'f_5', type: 'enum', label: { 'zh-TW': '狀態' }, blockId: 1, sequence: 1, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
  options: [11, 12].map((n) => ({ id: n, value: `o${n}`, label: { 'zh-TW': `狀態${n}` }, sequence: n, active: true })),
};
const mod: ModuleDetail = { id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1, blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [num] }] };
const wf: Workflow = { statusFieldId: 5, initialOptionId: 11, states: [{ optionId: 11, readOnly: false }, { optionId: 12, readOnly: false }], transitions: [] };
const def = (label = '財務'): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'a', kind: 'approval', name: { 'zh-TW': label }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e2', from: 'a', to: 'e' }],
});
const flow = (p: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key: 'po', name: { 'zh-TW': '採購流程' }, toOptionId: 12, priority: 1, active: true, used: false, pendingCount: 0, moduleKey: 'doc', version: 1, versionId: 3, definition: def(), graph: def(), ...p,
});

let router: ReturnType<typeof createRouter>;
async function mountAt(url: string, opts: { flow?: ApprovalFlow | 404; edit?: boolean; width?: number; flowError?: number; moduleError?: number; meError?: boolean } = {}) {
  window.innerWidth = opts.width ?? 1366;
  window.dispatchEvent(new Event('resize'));
  if (opts.moduleError) api.getModule.mockRejectedValue({ status: opts.moduleError }); else api.getModule.mockResolvedValue(mod);
  api.getWorkflow.mockResolvedValue(wf as never);
  if (opts.flow === 404 || opts.flowError) api.getApprovalFlow.mockRejectedValue({ status: opts.flowError ?? 404 });
  else api.getApprovalFlow.mockResolvedValue((opts.flow ?? flow()) as never);
  api.getApprovalFlows.mockResolvedValue([]);
  if (opts.meError) api.getMe.mockRejectedValue({ status: 500 });
  else api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: opts.edit ?? true, delete: true } } as never);
  api.getUsers.mockResolvedValue([]);
  api.getGroups.mockResolvedValue([]);
  api.getEffectivePolicies.mockResolvedValue({ values: {} } as never);
  api.getModulePolicies.mockResolvedValue([]);
  const stub = { template: '<i/>' };
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/system/dynamic-modules/:moduleKey', name: 'editor', component: stub },
      { path: '/system/dynamic-modules/:moduleKey/approval-flows/_new', name: 'flow-designer-new', component: FlowDesignerPage, meta: { collapseDrawer: true } },
      { path: '/system/dynamic-modules/:moduleKey/approval-flows/:flowKey', name: 'flow-designer', component: FlowDesignerPage, meta: { collapseDrawer: true } },
    ],
  });
  await router.push(url);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(RouterView))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [createPinia(), [quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
      stubs: { DesignerCanvas: CanvasStub },
    },
  });
  await new Promise((r) => setTimeout(r, 80));
  await flushPromises();
  return w;
}
const url = (key = 'po') => `/system/dynamic-modules/doc/approval-flows/${key}`;

describe('FlowDesignerPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { window.innerWidth = 1366; window.dispatchEvent(new Event('resize')); });

  it('loads the draft when there is one, else the published graph', async () => {
    const withDraft = await mountAt(url(), { flow: flow({ draft: { definition: def('草稿財務') }, draftRev: 'r1' }) });
    expect(withDraft.find('[data-testid="n-a"]').text()).toBe('草稿財務');
    withDraft.unmount();
    const published = await mountAt(url());
    expect(published.find('[data-testid="n-a"]').text()).toBe('財務');
    published.unmount();
  });

  it('a flow without layout is laid out left to right', async () => {
    const w = await mountAt(url());
    const xs = ['s', 'a', 'e'].map((id) => Number(w.get(`[data-testid="n-${id}"]`).attributes('data-x')));
    expect(xs[0]).toBeLessThan(xs[1]);
    expect(xs[1]).toBeLessThan(xs[2]);
    w.unmount();
  });

  it('uses the flow-keyed canvas id, and designer-new for a new flow', async () => {
    const w = await mountAt(url());
    expect(w.get('[data-testid="canvas"]').attributes('data-id')).toBe('designer-po');
    w.unmount();
    const n = await mountAt('/system/dynamic-modules/doc/approval-flows/_new?target=12');
    expect(n.get('[data-testid="canvas"]').attributes('data-id')).toBe('designer-new');
    expect(n.findAll('[data-testid^="n-"]').length).toBe(3);
    n.unmount();
  });

  it('the new-flow route is _new so a flow coded new still opens', async () => {
    const w = await mountAt(url('new'), { flow: flow({ key: 'new' }) });
    expect(api.getApprovalFlow).toHaveBeenCalledWith('new');
    expect(w.get('[data-testid="n-a"]').text()).toBe('財務');
    expect(router.currentRoute.value.name).toBe('flow-designer');
    w.unmount();
  });

  it('a wrong moduleKey in the url is replaced with the flow module', async () => {
    const w = await mountAt('/system/dynamic-modules/other/approval-flows/po', { flow: flow({ moduleKey: 'doc' }) });
    expect(router.currentRoute.value.params.moduleKey).toBe('doc');
    w.unmount();
  });

  it('dragging a node on a tablet moves it and marks unsaved', async () => {
    const w = await mountAt(url(), { width: 820 });
    expect(w.get('[data-testid="canvas"]').attributes('data-draggable')).toBe('true');
    expect(w.find('[data-testid="de-unsaved"]').exists()).toBe(false);
    await w.get('[data-testid="move-a"]').trigger('click');
    expect(w.get('[data-testid="n-a"]').attributes('data-x')).toBe('500');
    expect(w.find('[data-testid="de-unsaved"]').text()).toBe('未儲存');
    w.unmount();
  });

  it('phone: nodes are not draggable and tapping opens the properties dialog', async () => {
    const w = await mountAt(url(), { width: 390 });
    expect(w.get('[data-testid="canvas"]').attributes('data-draggable')).toBe('false');
    expect(document.body.querySelector('[data-testid="de-phone-panel"]')).toBeNull();
    await w.get('[data-testid="n-a"]').trigger('click');
    await flushPromises();
    expect(document.body.querySelector('[data-testid="de-phone-panel"]')).not.toBeNull();
    w.unmount();
  });

  it('phone first screen frames the first 3 nodes in topological order; tablet and up frame the whole graph (J66)', async () => {
    const phone = await mountAt(url(), { width: 390 });
    expect(JSON.parse(phone.get('[data-testid="canvas"]').attributes('data-initial')!)).toEqual(['s', 'a', 'e']);
    phone.unmount();
    const long = await mountAt(url(), { width: 390, flow: flow({ definition: { ...def(), nodes: [...def().nodes, { id: 'x', kind: 'notify' }], edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e3', from: 'a', to: 'x' }, { id: 'e4', from: 'x', to: 'e' }] }, graph: undefined }) });
    expect(JSON.parse(long.get('[data-testid="canvas"]').attributes('data-initial')!)).toEqual(['s', 'a', 'x']);
    long.unmount();
    const tablet = await mountAt(url(), { width: 820 });
    expect(tablet.get('[data-testid="canvas"]').attributes('data-initial')).toBe('null');
    tablet.unmount();
  });

  it('tablet: the panel is a drawer that opens on a node and closes on the empty canvas, and the node is scrolled into view', async () => {
    const w = await mountAt(url(), { width: 820 });
    expect(w.find('[data-testid="de-drawer"]').exists()).toBe(false);
    expect(w.find('[data-testid="de-dock"]').exists()).toBe(false);
    await w.get('[data-testid="n-a"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="de-drawer"]').exists()).toBe(true);
    expect(canvasCalls.focusNode).toHaveBeenCalledWith('a', 360);
    await w.get('[data-testid="pane"]').trigger('click');
    expect(w.find('[data-testid="de-drawer"]').exists()).toBe(false);
    w.unmount();
  });

  it('laptop and desktop dock the panel; desktop is wider', async () => {
    const laptop = await mountAt(url(), { width: 1366 });
    expect(laptop.get('[data-testid="de-dock"]').attributes('style')).toContain('360px');
    laptop.unmount();
    const desktop = await mountAt(url(), { width: 1920 });
    expect(desktop.get('[data-testid="de-dock"]').attributes('style')).toContain('440px');
    desktop.unmount();
  });

  it('read-only designer sees the graph with no drag; the toolbar has no undo', async () => {
    const w = await mountAt(url(), { edit: false });
    expect(w.get('[data-testid="canvas"]').attributes('data-draggable')).toBe('false');
    expect(w.find('[data-testid="de-readonly"]').exists()).toBe(true);
    expect(w.find('[data-testid="tb-undo"]').exists()).toBe(false);
    expect(w.find('[data-testid="tb-fit"]').exists()).toBe(true);
    await w.get('[data-testid="move-a"]').trigger('click');
    expect(w.find('[data-testid="de-unsaved"]').exists()).toBe(false);
    w.unmount();
  });

  it('a missing flow shows not found with a back button', async () => {
    const w = await mountAt(url('zzz'), { flow: 404 });
    expect(w.get('[data-testid="de-not-found"]').text()).toContain('找不到流程');
    w.unmount();
  });

  it('no permission shows the forbidden notice, not "load failed" (flow 403, module 403, read-only on _new)', async () => {
    for (const o of [{ flowError: 403 }, { moduleError: 403 }]) {
      const w = await mountAt(url(), o);
      expect(w.find('[data-testid="forbidden"]').exists()).toBe(true);
      expect(w.find('[data-testid="de-load-failed"]').exists()).toBe(false);
      w.unmount();
    }
    const ro = await mountAt('/system/dynamic-modules/doc/approval-flows/_new?target=12', { edit: false });
    expect(ro.find('[data-testid="forbidden"]').exists()).toBe(true);
    expect(ro.find('[data-testid="canvas"]').exists()).toBe(false);
    ro.unmount();
    const rw = await mountAt('/system/dynamic-modules/doc/approval-flows/_new?target=12');
    expect(rw.find('[data-testid="forbidden"]').exists()).toBe(false);
    rw.unmount();
  });

  it('a failed permission lookup is a load failure, not an endless spinner', async () => {
    const w = await mountAt(url(), { meError: true });
    expect(w.find('[data-testid="de-load-failed"]').exists()).toBe(true);
    w.unmount();
  });

  it('a stage-format (sequence) flow opened by url goes back to the flows tab; its only editor is the old dialog (U57)', async () => {
    const seq = flow({ definition: { shape: 'sequence', stages: [] } as never, graph: def() });
    const w = await mountAt(url(), { flow: seq });
    expect(router.currentRoute.value.fullPath).toBe('/system/dynamic-modules/doc?tab=approvalFlows&target=12');
    expect(w.find('[data-testid="canvas"]').exists()).toBe(false);
    w.unmount();
  });

  it('back goes to the flows tab of the module and focuses the target state', async () => {
    const w = await mountAt(url());
    await w.get('[data-testid="de-back"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe('/system/dynamic-modules/doc?tab=approvalFlows&target=12');
    w.unmount();
  });

  it('leaving in-app with unsaved changes offers leave or cancel (U52)', async () => {
    const w = await mountAt(url());
    await w.get('[data-testid="move-a"]').trigger('click');
    void router.push('/system/dynamic-modules/doc');
    await flushPromises();
    expect(router.currentRoute.value.name).toBe('flow-designer');
    const cancel = document.body.querySelector('[data-testid="de-leave-cancel"]') as HTMLElement;
    expect(cancel).not.toBeNull();
    cancel.click();
    await flushPromises();
    expect(router.currentRoute.value.name).toBe('flow-designer');
    void router.push('/system/dynamic-modules/doc');
    await flushPromises();
    (document.body.querySelector('[data-testid="de-leave-discard"]') as HTMLElement).click();
    await flushPromises();
    expect(router.currentRoute.value.name).toBe('editor');
    w.unmount();
  });

  it('leaving with nothing unsaved does not ask', async () => {
    const w = await mountAt(url());
    await router.push('/system/dynamic-modules/doc');
    expect(router.currentRoute.value.name).toBe('editor');
    w.unmount();
  });

  it('closing the tab with unsaved changes sets the native beforeunload prompt', async () => {
    const w = await mountAt(url());
    const clean = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);
    await w.get('[data-testid="move-a"]').trigger('click');
    const dirty = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(dirty);
    expect(dirty.defaultPrevented).toBe(true);
    w.unmount();
    const after = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(after);
    expect(after.defaultPrevented).toBe(false);
  });

  const nodeError = { errors: { 'definition.nodes[1].approvers': ['請選簽核人'] }, warnings: [], newVersion: false, pendingCount: 0 };

  it('validate marks the node and lists the issue; clicking the issue selects the node, pans to it and opens its panel', async () => {
    api.validateApprovalFlow.mockResolvedValue(nodeError);
    const w = await mountAt(url());
    expect(w.find('[data-testid="de-issues"]').exists()).toBe(false);
    await w.get('[data-testid="tb-validate"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-testid="tb-errors"]').text()).toContain('1');
    expect(w.find('[data-testid="de-issues"]').exists()).toBe(true);
    await w.get('[data-testid="de-issue-0"]').trigger('click');
    await flushPromises();
    expect(canvasCalls.focusNode).toHaveBeenCalledWith('a', 0);
    expect(w.get('[data-testid="de-panel-title"]').text()).toBe('財務');
    expect(w.get('[data-testid="de-panel"]').text()).toContain('請選簽核人');
    w.unmount();
  });

  it('save draft from the toolbar sends the working copy and clears the unsaved mark', async () => {
    api.saveApprovalFlowDraft.mockResolvedValue(flow({ draft: { definition: def() }, draftRev: 'r2' }) as never);
    const w = await mountAt(url());
    await w.get('[data-testid="move-a"]').trigger('click');
    expect(w.find('[data-testid="de-unsaved"]').exists()).toBe(true);
    await w.get('[data-testid="tb-save"]').trigger('click');
    await flushPromises();
    expect(api.saveApprovalFlowDraft).toHaveBeenCalledTimes(1);
    const body = api.saveApprovalFlowDraft.mock.calls[0][1];
    expect(body.layout?.nodes.a).toMatchObject({ x: 500 });
    expect(w.find('[data-testid="de-unsaved"]').exists()).toBe(false);
    w.unmount();
  });

  it('a flow that was published and has a draft can discard it after a confirmation', async () => {
    api.discardApprovalFlowDraft.mockResolvedValue(flow() as never);
    const w = await mountAt(url(), { flow: flow({ draft: { definition: def('草稿財務') }, draftRev: 'r1' }) });
    await w.get('[data-testid="tb-more"]').trigger('click');
    await new Promise((r) => setTimeout(r, 50));
    (document.body.querySelector('[data-testid="tb-discard"]') as HTMLElement).click();
    await flushPromises();
    expect(api.discardApprovalFlowDraft).not.toHaveBeenCalled();
    (document.body.querySelector('[data-testid="discard-confirm"]') as HTMLElement).click();
    await flushPromises();
    expect(api.discardApprovalFlowDraft).toHaveBeenCalledWith('po', 'r1', undefined);
    w.unmount();
  });

  it('the leave dialog can save the draft and leave', async () => {
    api.saveApprovalFlowDraft.mockResolvedValue(flow({ draft: { definition: def() }, draftRev: 'r2' }) as never);
    const w = await mountAt(url());
    await w.get('[data-testid="move-a"]').trigger('click');
    void router.push('/system/dynamic-modules/doc');
    await flushPromises();
    (document.body.querySelector('[data-testid="de-leave-save"]') as HTMLElement).click();
    await flushPromises();
    expect(api.saveApprovalFlowDraft).toHaveBeenCalledTimes(1);
    expect(router.currentRoute.value.name).toBe('editor');
    w.unmount();
  });

  it('saving a new flow replaces the url with the flow key', async () => {
    api.createApprovalFlowDraft.mockResolvedValue(flow({ key: 'new-flow', version: null, versionId: null, draft: { definition: def() }, draftRev: 'r0' }) as never);
    const w = await mountAt('/system/dynamic-modules/doc/approval-flows/_new?target=12');
    const fields = w.findComponent({ name: 'FlowBasicFields' });
    fields.vm.$emit('patch', { key: 'new-flow', name: { 'zh-TW': '新流程' } });
    await flushPromises();
    await w.get('[data-testid="tb-save"]').trigger('click');
    await flushPromises();
    expect(api.createApprovalFlowDraft).toHaveBeenCalledTimes(1);
    expect(router.currentRoute.value.name).toBe('flow-designer');
    expect(router.currentRoute.value.params.flowKey).toBe('new-flow');
    w.unmount();
  });

  it('the node list opens from the toolbar (also read-only), and picking an item selects the node, pans to it and opens its panel', async () => {
    const w = await mountAt(url(), { edit: false });
    expect(w.find('[data-testid="de-nodelist"]').exists()).toBe(false);
    await w.get('[data-testid="tb-nodelist"]').trigger('click');
    expect(w.find('[data-testid="de-nodelist"]').exists()).toBe(true);
    await w.get('[data-testid="de-nodelist-a"]').trigger('click');
    await flushPromises();
    expect(canvasCalls.focusNode).toHaveBeenCalledWith('a', 0);
    expect(w.get('[data-testid="de-panel-title"]').text()).toBe('財務');
    w.unmount();
  });
});
