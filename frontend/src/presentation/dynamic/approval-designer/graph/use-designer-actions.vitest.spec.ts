import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { REVALIDATE_MS, useDesignerActions } from './use-designer-actions';
import { useGraphDesigner } from './use-graph-designer';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    createApprovalFlowDraft: vi.fn(), saveApprovalFlowDraft: vi.fn(), discardApprovalFlowDraft: vi.fn(), publishApprovalFlow: vi.fn(),
    validateApprovalFlow: vi.fn(), deleteApprovalFlow: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e2', from: 'a', to: 'e' }],
});
const flow = (p: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 12, priority: 1, active: true, used: false, pendingCount: 0, moduleKey: 'doc', version: 1, versionId: 3, definition: def(), graph: def(), draftRev: 'r1', ...p,
});
const ok = { errors: {}, warnings: [], newVersion: false, pendingCount: 0 };
const nodeError = { errors: { 'definition.nodes[1].approvers': ['請選簽核人'] }, warnings: [], newVersion: false, pendingCount: 0 };
const warn = { errors: {}, warnings: [{ key: 'definition.nodes[1]', code: 'W1', message: '可能略過' }], newVersion: true, nextVersion: 2, pendingCount: 3 };

function setup(start: ApprovalFlow | null) {
  const ctx = { moduleKey: () => 'doc', flows: () => [] as ApprovalFlow[], reload: vi.fn(async () => start ?? undefined), onCreated: vi.fn(), onDeleted: vi.fn(), notify: vi.fn(), t: ((k: string) => k) as never };
  let designer!: ReturnType<typeof useGraphDesigner>;
  let actions!: ReturnType<typeof useDesignerActions>;
  const w = mount(defineComponent({ setup() { designer = useGraphDesigner(() => true); actions = useDesignerActions(designer, ctx); return () => null; } }), {
    global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } }) }]] },
  });
  designer.load(start, 12);
  if (!start) designer.editMeta({ key: 'po', name: { 'zh-TW': '採購' } });
  return { ctx, designer, actions, w };
}
const rename = (d: ReturnType<typeof useGraphDesigner>, name: string) => d.editMeta({ name: { 'zh-TW': name } });
const sentOf = (m: unknown, call = 0) => ((m as { mock: { calls: unknown[][] } }).mock.calls[call]);

beforeEach(() => { vi.clearAllMocks(); vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }); });
afterEach(() => { vi.useRealTimers(); });

describe('useDesignerActions', () => {
  it('no error marks before the first validate', async () => {
    const { designer, actions } = setup(flow());
    rename(designer, 'x');
    await vi.advanceTimersByTimeAsync(REVALIDATE_MS * 2);
    expect(api.validateApprovalFlow).not.toHaveBeenCalled();
    expect(designer.issues.value).toBeNull();
    expect(actions.errorCount.value).toBe(0);
  });

  it('after the first validate an edit re-validates after 0.8s and drops stale answers', async () => {
    api.validateApprovalFlow.mockResolvedValueOnce(ok);
    const { designer, actions } = setup(flow());
    await actions.validate();
    expect(api.validateApprovalFlow).toHaveBeenCalledTimes(1);
    // 兩次修改：第一次的驗證慢、第二次快；慢的後到，不得蓋掉新的結果
    let slow!: (v: unknown) => void;
    api.validateApprovalFlow.mockReturnValueOnce(new Promise((r) => { slow = r; }) as never);
    rename(designer, 'a');
    await vi.advanceTimersByTimeAsync(REVALIDATE_MS - 1);
    expect(api.validateApprovalFlow).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(api.validateApprovalFlow).toHaveBeenCalledTimes(2);
    api.validateApprovalFlow.mockResolvedValueOnce(nodeError);
    rename(designer, 'b');
    await vi.advanceTimersByTimeAsync(REVALIDATE_MS);
    expect(actions.errorCount.value).toBe(1);
    slow(ok);
    await flushPromises();
    expect(actions.errorCount.value).toBe(1);
    expect(designer.issues.value?.errors.nodes.a).toHaveLength(1);
  });

  it('live validation of a flow without a target state marks it quietly: no selection jump, no issue list', async () => {
    const { designer, actions } = setup(null);
    designer.editMeta({ toOptionId: null });
    designer.select({ kind: 'node', id: 'approval-1' });
    await actions.validate(); // 按驗證：有明確動作，拉回流程層並開清單
    expect(designer.selection.value).toEqual({ kind: 'flow' });
    expect(actions.issuesOpen.value).toBe(true);
    designer.select({ kind: 'node', id: 'approval-1' });
    actions.issuesOpen.value = false;
    designer.editMeta({ name: { 'zh-TW': 'x' } }); // 之後的修改停 0.8 秒重驗
    await vi.advanceTimersByTimeAsync(REVALIDATE_MS);
    expect(designer.issues.value?.errors.flow.length).toBeGreaterThan(0);
    expect(designer.selection.value).toEqual({ kind: 'node', id: 'approval-1' });
    expect(actions.issuesOpen.value).toBe(false);
  });

  it('publish with errors does not publish and opens the list', async () => {
    api.validateApprovalFlow.mockResolvedValue(nodeError);
    const { actions } = setup(flow());
    await actions.requestPublish();
    expect(api.publishApprovalFlow).not.toHaveBeenCalled();
    expect(actions.issuesOpen.value).toBe(true);
    expect(actions.publishInfo.value).toBeNull();
  });

  it('publish with only warnings asks, then publishes and clears the history', async () => {
    api.validateApprovalFlow.mockResolvedValue(warn);
    api.publishApprovalFlow.mockResolvedValue({ flow: flow({ version: 2, versionId: 4, draft: null, draftRev: 'r2' }), warnings: [] });
    const { designer, actions, ctx } = setup(flow());
    rename(designer, '大額');
    await actions.requestPublish();
    expect(actions.publishInfo.value).toMatchObject({ newVersion: true, nextVersion: 2, pendingCount: 3 });
    expect(actions.publishInfo.value!.warnings).toHaveLength(1);
    expect(api.publishApprovalFlow).not.toHaveBeenCalled();
    await actions.confirmPublish();
    expect(api.publishApprovalFlow).toHaveBeenCalledTimes(1);
    expect(sentOf(api.publishApprovalFlow)[0]).toBe('po');
    expect(designer.dirty.value).toBe(false);
    expect(designer.undoable.value).toBe(false);
    expect(designer.server.value.versionId).toBe(4);
    expect(ctx.notify).toHaveBeenCalledWith('positive', 'approval.designer.graph.published');
  });

  it('publish of a new flow creates the draft first, replaces the url, then publishes', async () => {
    api.validateApprovalFlow.mockResolvedValue(ok);
    const order: string[] = [];
    api.createApprovalFlowDraft.mockImplementation((async () => { order.push('create'); return flow({ version: null, versionId: null, draft: { definition: def() }, draftRev: 'r0' }); }) as never);
    api.publishApprovalFlow.mockImplementation((async () => { order.push('publish'); return { flow: flow({ draftRev: 'r1' }), warnings: [] }; }) as never);
    const { actions, ctx } = setup(null);
    ctx.onCreated.mockImplementation(() => { order.push('replace'); });
    await actions.requestPublish();
    await actions.confirmPublish();
    expect(order).toEqual(['create', 'replace', 'publish']);
    expect(sentOf(api.publishApprovalFlow)[0]).toBe('po');
    expect(sentOf(api.createApprovalFlowDraft)[0]).toBe('doc');
    // 新流程發布只通知一次「已發布」，中間那次建草稿不另外跳「草稿已儲存」
    expect(ctx.notify.mock.calls.map((c) => c[1])).toEqual(['approval.designer.graph.published']);
  });

  it('a publish 400 after a clean validate opens the same issue list', async () => {
    api.validateApprovalFlow.mockResolvedValue(ok);
    api.publishApprovalFlow.mockRejectedValue({ status: 400, errors: { 'definition.nodes[1].approvers': ['簽核人已停用'] } });
    const { designer, actions } = setup(flow());
    await actions.requestPublish();
    await actions.confirmPublish();
    expect(actions.issuesOpen.value).toBe(true);
    expect(designer.issues.value?.errors.nodes.a?.[0].message).toBe('簽核人已停用');
  });

  it('a failed first publish leaves a draft-only flow and says so', async () => {
    api.validateApprovalFlow.mockResolvedValue(ok);
    api.createApprovalFlowDraft.mockResolvedValue(flow({ version: null, versionId: null, draft: { definition: def() }, draftRev: 'r0' }) as never);
    api.publishApprovalFlow.mockRejectedValue({ status: 400, errors: { 'definition.nodes[1].approvers': ['x'] } });
    const { designer, actions, ctx } = setup(null);
    await actions.requestPublish();
    await actions.confirmPublish();
    expect(designer.server.value.isNew).toBe(false);
    expect(ctx.notify).toHaveBeenCalledWith('info', 'approval.designer.graph.firstPublishFailed');
    expect(ctx.onCreated).toHaveBeenCalledWith('po');
  });

  it('save draft: a new flow POSTs, an existing one PUTs with baseRev and baseVersionId; unsaved clears but history stays (U60)', async () => {
    api.saveApprovalFlowDraft.mockResolvedValue(flow({ draft: { definition: def() }, draftRev: 'r2' }) as never);
    const { designer, actions } = setup(flow());
    rename(designer, '大額');
    expect(designer.dirty.value).toBe(true);
    await actions.saveDraft();
    const [key, body] = sentOf(api.saveApprovalFlowDraft) as [string, Record<string, unknown>];
    expect(key).toBe('po');
    expect(body).toMatchObject({ baseRev: 'r1', baseVersionId: 3, key: 'po', toOptionId: 12, name: { 'zh-TW': '大額' } });
    expect(designer.dirty.value).toBe(false);
    expect(designer.undoable.value).toBe(true);
    expect(designer.server.value.draftRev).toBe('r2');
  });

  it('edits made while a save or publish request is in flight stay unsaved (only the sent snapshot is marked saved)', async () => {
    let done!: (v: unknown) => void;
    api.saveApprovalFlowDraft.mockReturnValueOnce(new Promise((r) => { done = r; }) as never);
    const { designer, actions } = setup(flow());
    rename(designer, 'sent');
    const saving = actions.saveDraft();
    rename(designer, 'edited while saving');
    done(flow({ draft: { definition: def() }, draftRev: 'r2' }));
    await saving;
    expect(designer.dirty.value).toBe(true);
    await vi.advanceTimersByTimeAsync(0);
    api.saveApprovalFlowDraft.mockResolvedValueOnce(flow({ draftRev: 'r3' }) as never);
    await actions.saveDraft();
    expect(designer.dirty.value).toBe(false);

    // 發布同理，而且發布成功後復原堆疊清空、未存的修改仍在
    api.validateApprovalFlow.mockResolvedValue(ok);
    api.publishApprovalFlow.mockReturnValueOnce(new Promise((r) => { done = r; }) as never);
    rename(designer, 'to publish');
    const publishing = actions.confirmPublish();
    rename(designer, 'edited while publishing');
    done({ flow: flow({ version: 2, versionId: 4, draft: null, draftRev: 'r4' }), warnings: [] });
    await publishing;
    expect(designer.dirty.value).toBe(true);
  });

  it('a new flow without a target state is not sent and is marked on the flow level', async () => {
    const { designer, actions } = setup(null);
    designer.editMeta({ toOptionId: null });
    await actions.saveDraft();
    expect(api.createApprovalFlowDraft).not.toHaveBeenCalled();
    expect(designer.issues.value?.errors.flow.length).toBeGreaterThan(0);
  });

  it('a 409 conflict opens the dialog; overwrite sends force with the tokens from the response', async () => {
    api.saveApprovalFlowDraft.mockRejectedValueOnce({ status: 409, code: 'DraftConflict', params: { draftUpdatedByName: 'Amy', draftUpdatedTime: '2026-11-16T08:00:00Z', draftRev: 'r9', versionId: 4 } });
    api.saveApprovalFlowDraft.mockResolvedValueOnce(flow({ draftRev: 'r10', versionId: 4 }) as never);
    const { designer, actions } = setup(flow());
    rename(designer, 'x');
    await actions.saveDraft();
    expect(actions.conflict.value?.info.draftUpdatedByName).toBe('Amy');
    await actions.resolveConflict('overwrite');
    expect(actions.conflict.value).toBeNull();
    expect(sentOf(api.saveApprovalFlowDraft, 1)[1]).toMatchObject({ force: true, baseRev: 'r9', baseVersionId: 4 });
    expect(designer.dirty.value).toBe(false);
  });

  it('conflict: reload drops my changes; cancel keeps them', async () => {
    api.saveApprovalFlowDraft.mockRejectedValue({ status: 409, code: 'DraftConflict', params: { draftRev: 'r9' } });
    const { designer, actions, ctx } = setup(flow());
    rename(designer, 'mine');
    await actions.saveDraft();
    await actions.resolveConflict('cancel');
    expect(designer.dirty.value).toBe(true);
    await actions.saveDraft();
    await actions.resolveConflict('reload');
    expect(ctx.reload).toHaveBeenCalled();
    expect(designer.dirty.value).toBe(false);
    expect(designer.meta.value!.name).toEqual({ 'zh-TW': '採購' });
  });

  it('discard sends the base rev, reloads the published graph and clears history', async () => {
    api.discardApprovalFlowDraft.mockResolvedValue(flow({ draft: null }) as never);
    const { designer, actions, ctx } = setup(flow({ draft: { definition: def() } }));
    rename(designer, 'x');
    expect(designer.undoable.value).toBe(true);
    await actions.discard();
    expect(sentOf(api.discardApprovalFlowDraft)).toEqual(['po', 'r1', undefined]);
    expect(ctx.reload).toHaveBeenCalled();
    expect(designer.undoable.value).toBe(false);
    expect(designer.dirty.value).toBe(false);
  });

  it('discard does nothing for a draft-only flow; delete flow only works there (U64)', async () => {
    api.deleteApprovalFlow.mockResolvedValue(undefined as never);
    const draftOnly = flow({ version: null, versionId: null, definition: null as never, graph: null as never, draft: { definition: def() } });
    const { designer, actions, ctx } = setup(draftOnly);
    await actions.discard();
    expect(api.discardApprovalFlowDraft).not.toHaveBeenCalled();
    await actions.deleteFlow();
    expect(api.deleteApprovalFlow).toHaveBeenCalledWith('po');
    expect(ctx.onDeleted).toHaveBeenCalled();
    expect(designer.dirty.value).toBe(false);
    const pub = setup(flow());
    await pub.actions.deleteFlow();
    expect(api.deleteApprovalFlow).toHaveBeenCalledTimes(1);
  });

  it('a read-only designer cannot save, publish, discard or delete', async () => {
    const ctx = { moduleKey: () => 'doc', flows: () => [], reload: vi.fn(), onCreated: vi.fn(), onDeleted: vi.fn(), notify: vi.fn(), t: ((k: string) => k) as never };
    let designer!: ReturnType<typeof useGraphDesigner>;
    let actions!: ReturnType<typeof useDesignerActions>;
    mount(defineComponent({ setup() { designer = useGraphDesigner(() => false); actions = useDesignerActions(designer, ctx); return () => null; } }), {
      global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] },
    });
    designer.load(flow());
    await actions.saveDraft(); await actions.requestPublish(); await actions.discard(); await actions.deleteFlow();
    expect(Object.values(api).some((f) => (f as ReturnType<typeof vi.fn>).mock.calls.length > 0)).toBe(false);
  });
});
