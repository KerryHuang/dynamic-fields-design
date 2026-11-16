import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import type { ApprovalFlow, ApprovalFlowDraftInput, ApprovalFlowInput, DraftConflictInfo } from 'src/domain/dynamic/approval-flow-types';
import { bodyOf, HttpFail, mockHttp } from 'src/test-utils/mock-http';
import {
  useApprovalFlow, useCreateFlowDraft, useDeleteApprovalFlow, useDiscardFlowDraft, usePublishFlow, useReorderApprovalFlows,
  useSaveApprovalFlow, useSaveFlowDraft, useValidateFlow,
} from './approval-flow-queries';

const input: ApprovalFlowInput = {
  key: 'po-review', name: { 'zh-TW': '採購簽核' }, toOptionId: 12, priority: 1, active: true,
  definition: { shape: 'sequence', stages: [] },
};

function setup() {
  const qc = new QueryClient();
  const spy = vi.spyOn(qc, 'invalidateQueries');
  let m!: { save: ReturnType<typeof useSaveApprovalFlow>; del: ReturnType<typeof useDeleteApprovalFlow>; order: ReturnType<typeof useReorderApprovalFlows> };
  mount(defineComponent({
    setup() { m = { save: useSaveApprovalFlow('po'), del: useDeleteApprovalFlow('po'), order: useReorderApprovalFlows('po') }; return () => null; },
  }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
  const keys = () => spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey));
  return { m, keys };
}

describe('approval flow mutations', () => {
  it('create posts module query and input', async () => {
    const calls = mockHttp(() => input);
    await setup().m.save.mutateAsync({ input });
    expect(calls[0]).toMatchObject({ method: 'post', url: '/approval-flows', params: { module: 'po' } });
    expect(bodyOf(calls[0])).toEqual(input);
  });

  it('update puts to the flow key', async () => {
    const calls = mockHttp(() => input);
    await setup().m.save.mutateAsync({ key: 'po-review', input });
    expect(calls[0]).toMatchObject({ method: 'put', url: '/approval-flows/po-review' });
  });

  it('reorder puts module, toOptionId and keys', async () => {
    const calls = mockHttp(() => []);
    await setup().m.order.mutateAsync({ toOptionId: 12, keys: ['b', 'a'] });
    expect(calls[0]).toMatchObject({ method: 'put', url: '/approval-flows/order' });
    expect(bodyOf(calls[0])).toEqual({ module: 'po', toOptionId: 12, keys: ['b', 'a'] });
  });

  it('update and delete also invalidate the single-flow cache of that key (its `active`, version and draft go stale)', async () => {
    mockHttp(() => ({}));
    const { m, keys } = setup();
    await m.save.mutateAsync({ key: 'po-review', input });
    await m.del.mutateAsync('po-old');
    await flushPromises();
    expect(keys()).toEqual(expect.arrayContaining([JSON.stringify(['approval-flow', 'po-review']), JSON.stringify(['approval-flow', 'po-old'])]));
  });

  it.each(['save', 'del', 'order'] as const)('%s invalidates approval-flows and workflow of the module', async (which) => {
    mockHttp(() => ({}));
    const { m, keys } = setup();
    if (which === 'save') await m.save.mutateAsync({ input });
    else if (which === 'del') await m.del.mutateAsync('po-review');
    else await m.order.mutateAsync({ toOptionId: 12, keys: [] });
    await flushPromises();
    expect(keys()).toEqual(expect.arrayContaining([JSON.stringify(['approval-flows', 'po']), JSON.stringify(['workflow', 'po'])]));
  });
});

const draftInput: ApprovalFlowDraftInput = {
  key: 'po-big', name: { 'zh-TW': '大額採購' }, toOptionId: 12, priority: 2,
  definition: { shape: 'graph', stages: [], nodes: [{ id: 'start', kind: 'start' }, { id: 'end', kind: 'end' }], edges: [{ id: 'e1', from: 'start', to: 'end' }] },
  layout: { nodes: { start: { x: 0, y: 0 }, end: { x: 224, y: 0 } } },
  baseRev: 'abc123', baseVersionId: 7,
};

/** The error a call rejects with (an ApiError after the interceptor). */
const rejection = async <T>(p: Promise<unknown>): Promise<T> => p.then(() => { throw new Error('expected a rejection'); }, (e: unknown) => e as T);

function setupDraft() {
  const qc = new QueryClient();
  const spy = vi.spyOn(qc, 'invalidateQueries');
  let m!: {
    create: ReturnType<typeof useCreateFlowDraft>; save: ReturnType<typeof useSaveFlowDraft>; discard: ReturnType<typeof useDiscardFlowDraft>;
    publish: ReturnType<typeof usePublishFlow>; validate: ReturnType<typeof useValidateFlow>;
  };
  mount(defineComponent({
    setup() {
      m = { create: useCreateFlowDraft('po'), save: useSaveFlowDraft('po'), discard: useDiscardFlowDraft('po'), publish: usePublishFlow('po'), validate: useValidateFlow('po') };
      return () => null;
    },
  }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
  const keys = () => spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey));
  return { m, keys };
}

describe('approval flow draft endpoints', () => {
  it('draft endpoints send the base rev and version id', async () => {
    const calls = mockHttp(() => ({}));
    const { m } = setupDraft();
    await m.create.mutateAsync(draftInput);
    await m.save.mutateAsync({ key: 'po-big', input: draftInput });
    await m.publish.mutateAsync({ key: 'po-big', input: draftInput });
    expect(calls[0]).toMatchObject({ method: 'post', url: '/approval-flows/draft', params: { module: 'po' } });
    expect(calls[1]).toMatchObject({ method: 'put', url: '/approval-flows/po-big/draft' });
    expect(calls[2]).toMatchObject({ method: 'post', url: '/approval-flows/po-big/publish' });
    for (const c of calls) expect(bodyOf(c)).toMatchObject({ baseRev: 'abc123', baseVersionId: 7, key: 'po-big' });
  });

  it('discard sends the base rev and force', async () => {
    const calls = mockHttp(() => ({}));
    const { m } = setupDraft();
    await m.discard.mutateAsync({ key: 'po-big', baseRev: 'abc123' });
    await m.discard.mutateAsync({ key: 'po-big', baseRev: 'abc123', force: true });
    expect(calls[0]).toMatchObject({ method: 'delete', url: '/approval-flows/po-big/draft', params: { baseRev: 'abc123' } });
    expect(calls[0].params).not.toHaveProperty('force', true);
    expect(calls[1].params).toEqual({ baseRev: 'abc123', force: true });
  });

  it('a 409 draft conflict surfaces the code and the current editor from params (the shape the backend sends)', async () => {
    // 後端 ServiceResult.Conflict(msg, "DraftConflict", DraftConflictInfo)：code 與 params 在本文最上層，toApiError 只留這兩個
    const info: DraftConflictInfo = {
      draftUpdatedBy: 'wang', draftUpdatedByName: '王小明', draftUpdatedTime: '2026-11-16T08:00:00Z', draftRev: 'rev-2', versionId: 7,
    };
    mockHttp(() => new HttpFail(409, { message: 'conflict', code: 'DraftConflict', params: info }));
    const { m } = setupDraft();
    const err = await rejection<{ status: number; code: string; params: DraftConflictInfo }>(m.save.mutateAsync({ key: 'po-big', input: draftInput }));
    expect(err).toMatchObject({ status: 409, code: 'DraftConflict', params: info });
    // 「覆蓋」要送的基準就在 params 裡
    expect(err.params.draftRev).toBe('rev-2');
    expect(err.params.versionId).toBe(7);
  });

  it('a draft-only flow\'s 409 params omit versionId (null is not written)', async () => {
    mockHttp(() => new HttpFail(409, { message: 'conflict', code: 'DraftConflict', params: { draftUpdatedBy: 'wang', draftUpdatedByName: '王小明', draftUpdatedTime: '2026-11-16T08:00:00Z', draftRev: 'rev-2' } }));
    const { m } = setupDraft();
    const err = await rejection<{ params: DraftConflictInfo }>(m.save.mutateAsync({ key: 'po-big', input: draftInput }));
    expect(err.params.versionId).toBeUndefined();
  });

  it('a duplicate key on POST draft is a 409 with code FlowKeyTaken', async () => {
    mockHttp(() => new HttpFail(409, { message: 'taken', code: 'FlowKeyTaken' }));
    const { m } = setupDraft();
    await expect(m.create.mutateAsync(draftInput)).rejects.toMatchObject({ status: 409, code: 'FlowKeyTaken' });
  });

  it('the versionId the backend returns with a flow is what goes into baseVersionId', async () => {
    // 後端單筆流程回應帶 versionId（目前版本 Id）；沒有它，已發布流程的每次存草稿都會 409
    const flow: Partial<ApprovalFlow> = { key: 'po-big', versionId: 7, draftRev: 'rev-1' };
    const calls = mockHttp((c) => (c.method === 'get' ? flow : {}));
    const qc = new QueryClient();
    let q!: ReturnType<typeof useApprovalFlow>;
    let m!: ReturnType<typeof useSaveFlowDraft>;
    mount(defineComponent({ setup() { q = useApprovalFlow(() => 'po-big'); m = useSaveFlowDraft('po'); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
    await flushPromises();
    const loaded = q.data.value!;
    await m.mutateAsync({ key: 'po-big', input: { ...draftInput, baseRev: loaded.draftRev, baseVersionId: loaded.versionId } });
    expect(bodyOf(calls.find((c) => c.method === 'put'))).toMatchObject({ baseRev: 'rev-1', baseVersionId: 7 });
  });

  it('validate\'s nextVersion may be absent (the backend omits null)', async () => {
    mockHttp(() => ({ errors: {}, warnings: [], newVersion: false, pendingCount: 0 }));
    const { m } = setupDraft();
    const got = await m.validate.mutateAsync({ key: null, input: draftInput });
    expect(got.nextVersion).toBeUndefined();
  });

  it('validate passes the flow key only for an existing flow', async () => {
    const calls = mockHttp(() => ({ errors: {}, warnings: [], newVersion: false, nextVersion: null, pendingCount: 0 }));
    const { m } = setupDraft();
    await m.validate.mutateAsync({ key: 'po-big', input: draftInput });
    await m.validate.mutateAsync({ key: null, input: draftInput });
    expect(calls[0]).toMatchObject({ method: 'post', url: '/approval-flows/validate', params: { module: 'po', key: 'po-big' } });
    expect(calls[1].params).toEqual({ module: 'po' });
  });

  it.each(['create', 'save', 'discard', 'publish'] as const)('%s invalidates the list, the single flow and workflow; validate invalidates nothing', async (which) => {
    mockHttp(() => ({}));
    const { m, keys } = setupDraft();
    if (which === 'create') await m.create.mutateAsync(draftInput);
    else if (which === 'save') await m.save.mutateAsync({ key: 'po-big', input: draftInput });
    else if (which === 'discard') await m.discard.mutateAsync({ key: 'po-big', baseRev: null });
    else await m.publish.mutateAsync({ key: 'po-big', input: draftInput });
    await flushPromises();
    expect(keys()).toEqual(expect.arrayContaining([
      JSON.stringify(['approval-flows', 'po']), JSON.stringify(['approval-flow', 'po-big']), JSON.stringify(['workflow', 'po']),
    ]));
  });

  it('validate does not invalidate', async () => {
    mockHttp(() => ({ errors: {}, warnings: [], newVersion: false, nextVersion: null, pendingCount: 0 }));
    const { m, keys } = setupDraft();
    await m.validate.mutateAsync({ key: null, input: draftInput });
    await flushPromises();
    expect(keys()).toEqual([]);
  });
});

describe('useApprovalFlow', () => {
  it('loads one flow by key and stays idle without a key', async () => {
    const calls = mockHttp(() => ({ key: 'po-big' }));
    const qc = new QueryClient();
    let q!: ReturnType<typeof useApprovalFlow>;
    mount(defineComponent({ setup() { q = useApprovalFlow(() => 'po-big'); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
    await flushPromises();
    expect(calls[0]).toMatchObject({ method: 'get', url: '/approval-flows/po-big' });
    expect(q.data.value).toMatchObject({ key: 'po-big' });
    const idle = mockHttp(() => ({}));
    mount(defineComponent({ setup() { useApprovalFlow(() => null); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] } });
    await flushPromises();
    expect(idle).toHaveLength(0);
  });
});
