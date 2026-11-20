import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, nextTick } from 'vue';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow, ApproverSource, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { ApprovalPreview } from 'src/domain/dynamic/approval-types';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DesignerTrialPanel from './DesignerTrialPanel.vue';
import type { TrialOverlay } from './designer-trial';
import { useGraphDesigner } from './use-graph-designer';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), previewApprovalDraft: vi.fn(), getPagedRecords: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string): DesignerField => ({
  id, key: `f_${id}`, type: 'decimal', label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active',
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
});
const mod: ModuleDetail = { id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1, blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [field(7, '總額')] }] };
const appr = (id: string, approvers: ApproverSource = { source: 'manager', level: 1 }) => ({ id, kind: 'approval' as const, name: { 'zh-TW': id }, approvers, mode: 'any' as const, editableFields: [], onEmpty: 'fallback' as const });
const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'c', kind: 'condition' }, appr('a1'), appr('a2'), { id: 'e', kind: 'end' }],
  edges: [
    { id: 'e1', from: 's', to: 'c' }, { id: 'e2', from: 'c', to: 'a1', order: 1, when: { all: [{ field: 7, op: 'gte', value: 100 }] } }, { id: 'e3', from: 'c', to: 'a2', default: true },
    { id: 'e4', from: 'a1', to: 'e' }, { id: 'e5', from: 'a2', to: 'e' },
  ],
});
const flow = (p: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key: 'mine', name: { 'zh-TW': '我的' }, toOptionId: 12, priority: 2, active: true, used: false, pendingCount: 0, moduleKey: 'doc', version: 1, versionId: 3, definition: def(), graph: def(), ...p,
});
const preview = (p: Partial<ApprovalPreview> = {}): ApprovalPreview => ({
  outcome: 'flow', flowName: { 'zh-TW': '我的' }, flowKey: 'mine', picks: [],
  stages: [{ code: 'a2', name: { 'zh-TW': 'a2' }, status: 'enter', approvers: [{ userId: 'u1', displayName: '王小明' }], usesFallback: false }],
  path: { nodeIds: ['s', 'c', 'a2', 'e'], edgeIds: ['e1', 'e3', 'e5'] }, ...p,
});

type Host = { open: boolean; overlays: (TrialOverlay | null)[]; closed: number; canvas: number };
function mountPanel(opts: { canEdit?: boolean; phone?: boolean; start?: ApprovalFlow | null; flows?: ApprovalFlow[] } = {}) {
  api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
  const host: Host = { open: true, overlays: [], closed: 0, canvas: 0 };
  let designer!: ReturnType<typeof useGraphDesigner>;
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = defineComponent({
    components: { DesignerTrialPanel },
    props: { open: { type: Boolean, default: true } },
    setup() { designer = useGraphDesigner(() => opts.canEdit ?? true); designer.load(opts.start === undefined ? flow() : opts.start, 12); return { designer, host, flows: opts.flows ?? [], phone: opts.phone ?? false, mod }; },
    template: `<DesignerTrialPanel :designer="designer" :module="mod" :flows="flows" :phone="phone" :open="open"
      @overlay="(o) => host.overlays.push(o)" @close="host.closed++" @show-on-canvas="host.canvas++" />`,
  });
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  return { w, host, designer: () => designer };
}
const el = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const text = (id: string) => el(id)?.textContent ?? '';
async function settle() { await flushPromises(); await nextTick(); }
async function chooseInitiator(w: ReturnType<typeof mountPanel>['w']) {
  w.findAllComponents(quasar.QSelect).find((c) => c.props('label') === '發起人')!.vm.$emit('update:modelValue', 'u1');
  await settle();
}
async function run() { el('trial-run')!.click(); await settle(); }
const last = (h: Host) => h.overlays[h.overlays.length - 1];

describe('DesignerTrialPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    api.getPagedRecords.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  });

  it('sends the working copy (graph definition, name, target, priority) as an active draft with the initiator and values', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview());
    const { w, designer } = mountPanel({ flows: [flow({ key: 'other', priority: 5 })] });
    await settle();
    designer().editMeta({ name: { 'zh-TW': '改過的名稱' } });
    await chooseInitiator(w);
    expect(el('trial-run')!.hasAttribute('disabled')).toBe(false);
    await run();
    const [module, body] = api.previewApprovalDraft.mock.calls[0];
    expect(module).toBe('doc');
    expect(body).toMatchObject({ initiatorUserId: 'u1', draft: { key: 'mine', name: { 'zh-TW': '改過的名稱' }, toOptionId: 12, priority: 2, active: true } });
    expect(body.draft.definition.shape).toBe('graph');
    expect((body.draft.definition as GraphDefinition).nodes.map((n) => n.id)).toEqual(['s', 'c', 'a1', 'a2', 'e']);
    // the condition field is asked for, from the graph's edges
    expect(el('trial-field-7')).not.toBeNull();
  });

  it('the result becomes the overlay: path highlighted, others dimmed, approvers on the node', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview());
    const { w, host } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    const o = last(host)!;
    expect(Object.fromEntries(Object.entries(o.nodes).map(([k, v]) => [k, v.state]))).toEqual({ s: 'path', c: 'path', a1: 'off', a2: 'path', e: 'path' });
    expect(o.nodes.a2.approvers).toEqual(['王小明']);
    expect(o.edges.e2).toBe('off');
    expect(text('trial-flow')).toContain('我的');
    expect(el('trial-legend')).not.toBeNull();
  });

  it('closing the trial clears the overlay and the result, but keeps the chosen initiator', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview());
    const { w, host } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    expect(last(host)).not.toBeNull();
    await w.setProps({ open: false });
    await settle();
    expect(last(host)).toBeNull();
    expect(el('trial-result')).toBeNull();
    expect(el('trial-run')!.hasAttribute('disabled')).toBe(false);
  });

  it('an edit makes the result stale: the overlay goes away', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview());
    const { w, host, designer } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    expect(last(host)).not.toBeNull();
    designer().editMeta({ name: { 'zh-TW': '又改了' } });
    await settle();
    expect(last(host)).toBeNull();
    expect(el('trial-result')).toBeNull();
  });

  it('moving a node does not invalidate the trial (positions are not sent)', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview());
    const { w, host, designer } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    designer().move('a1', { x: 900, y: 40 });
    await settle();
    expect(last(host)).not.toBeNull();
    expect(el('trial-result')).not.toBeNull();
  });

  it('a response that arrives after an edit is dropped', async () => {
    let resolve!: (v: ApprovalPreview) => void;
    api.previewApprovalDraft.mockReturnValue(new Promise((r) => { resolve = r; }) as never);
    const { w, host, designer } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    designer().editMeta({ name: { 'zh-TW': '改了' } });
    await settle();
    resolve(preview());
    await settle();
    expect(el('trial-result')).toBeNull();
    expect(last(host)).toBeNull();
  });

  it('read-only designer can run a trial (U62)', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview());
    const { w, host } = mountPanel({ canEdit: false });
    await settle();
    await chooseInitiator(w);
    await run();
    expect(api.previewApprovalDraft).toHaveBeenCalledTimes(1);
    expect(last(host)).not.toBeNull();
  });

  it('phone offers the list view (the stages) and a way back to the canvas overlay', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({ stages: [
      { code: 'a2', name: { 'zh-TW': '財務' }, status: 'enter', approvers: [{ userId: 'u1', displayName: '王小明' }], usesFallback: false },
    ] }));
    const { w, host } = mountPanel({ phone: true });
    await settle();
    await chooseInitiator(w);
    await run();
    expect(el('preview-stages')).not.toBeNull();
    expect(text('preview-stage-a2')).toContain('王小明');
    el('trial-show-on-canvas')!.click();
    expect(host.canvas).toBe(1);
    // the button is for phones only
    document.body.innerHTML = '';
    const desktop = mountPanel({ phone: false });
    await settle();
    await chooseInitiator(desktop.w);
    await run();
    expect(el('trial-show-on-canvas')).toBeNull();
    expect(el('preview-stages')).not.toBeNull();
  });

  it('a result for another flow, a block or a direct transition has no overlay; the list says so', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({ flowKey: 'other', flowName: { 'zh-TW': '別條' }, path: { nodeIds: ['s'], edgeIds: [] } }));
    const { w, host } = mountPanel({ flows: [flow({ key: 'other', name: { 'zh-TW': '別條' }, priority: 1 })] });
    await settle();
    await chooseInitiator(w);
    await run();
    expect(last(host)).toBeNull();
    expect(text('trial-other-flow')).toContain('別條');
    expect(el('trial-list-only')).not.toBeNull();
    api.previewApprovalDraft.mockResolvedValue(preview({ outcome: 'blocked', stages: [], path: null }));
    await run();
    expect(el('trial-blocked')).not.toBeNull();
    expect(last(host)).toBeNull();
  });

  it('conditional reveals stop at the condition node (undecided), the rest is dimmed', async () => {
    api.previewApprovalDraft.mockResolvedValue(preview({ stages: [{ code: 'c', name: {}, status: 'conditional', approvers: [], usesFallback: false }], path: { nodeIds: ['s', 'c'], edgeIds: ['e1'] } }));
    const { w, host } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    const o = last(host)!;
    expect(o.nodes.c.state).toBe('undecided');
    expect(o.nodes.a2.state).toBe('off');
  });

  it('a 400 lists the problems by node name; other failures say the trial failed', async () => {
    api.previewApprovalDraft.mockRejectedValue({ status: 400, errors: { 'definition.nodes.a1.approvers': ['請選簽核人'], 'definition.graph': ['至少要有一個簽核節點'] } });
    const { w } = mountPanel();
    await settle();
    await chooseInitiator(w);
    await run();
    expect(text('trial-errors')).toContain('a1：請選簽核人');
    expect(text('trial-errors')).toContain('至少要有一個簽核節點');
    api.previewApprovalDraft.mockRejectedValue({ status: 500 });
    await run();
    expect(el('trial-failed')).not.toBeNull();
    expect(el('trial-errors')).toBeNull();
  });

  it('without a target state there is nothing to try', async () => {
    const { designer } = mountPanel();
    designer().load(null, null); // a new flow opened without a target state
    await settle();
    expect(el('trial-need-target')).not.toBeNull();
    expect(el('trial-run')).toBeNull();
  });

  it('the close button asks the page to end the trial', async () => {
    const { host } = mountPanel();
    await settle();
    el('de-trial-close')!.click();
    expect(host.closed).toBe(1);
  });
});
