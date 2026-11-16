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
  dynamicApi: { getModule: vi.fn(), getWorkflow: vi.fn(), getApprovalFlow: vi.fn(), getApprovalFlows: vi.fn(), getMe: vi.fn(), getUsers: vi.fn(), getGroups: vi.fn(), getEffectivePolicies: vi.fn(), getModulePolicies: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const canvasCalls = vi.hoisted(() => ({ fitAll: vi.fn(), focusNode: vi.fn() }));
// 真實的畫布只在瀏覽器驗；這裡的替身把收到的節點與線寫成文字，並提供觸發事件的按鈕。
const CanvasStub = defineComponent({
  name: 'DesignerCanvas', props: ['canvasId', 'nodes', 'edges', 'draggable'], emits: ['node-open', 'edge-open', 'pane-click', 'node-move'],
  setup(p, { emit, expose }) {
    expose(canvasCalls);
    return () => h('div', { 'data-testid': 'canvas', 'data-id': p.canvasId, 'data-draggable': String(p.draggable) }, [
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
async function mountAt(url: string, opts: { flow?: ApprovalFlow | 404; edit?: boolean; width?: number } = {}) {
  window.innerWidth = opts.width ?? 1366;
  window.dispatchEvent(new Event('resize'));
  api.getModule.mockResolvedValue(mod);
  api.getWorkflow.mockResolvedValue(wf as never);
  if (opts.flow === 404) api.getApprovalFlow.mockRejectedValue({ status: 404 });
  else api.getApprovalFlow.mockResolvedValue((opts.flow ?? flow()) as never);
  api.getApprovalFlows.mockResolvedValue([]);
  api.getMe.mockResolvedValue({ designer: { read: true, add: true, edit: opts.edit ?? true, delete: true } } as never);
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
});
