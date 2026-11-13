import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import type { ApprovalFlowInput } from 'src/domain/dynamic/approval-flow-types';
import { bodyOf, mockHttp } from 'src/test-utils/mock-http';
import { useDeleteApprovalFlow, useReorderApprovalFlows, useSaveApprovalFlow } from './approval-flow-queries';

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
