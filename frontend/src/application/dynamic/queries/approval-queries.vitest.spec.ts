import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { bodyOf, mockHttp, type Call } from 'src/test-utils/mock-http';
import { useApproveTask, useRejectTask, useSubmitApproval, useWithdrawApproval } from './approval-queries';

function setup() {
  const qc = new QueryClient();
  const spy = vi.spyOn(qc, 'invalidateQueries');
  let m!: { submit: ReturnType<typeof useSubmitApproval>; approve: ReturnType<typeof useApproveTask>; reject: ReturnType<typeof useRejectTask>; withdraw: ReturnType<typeof useWithdrawApproval> };
  mount(defineComponent({
    setup() { m = { submit: useSubmitApproval(), approve: useApproveTask(), reject: useRejectTask(), withdraw: useWithdrawApproval() }; return () => null; },
  }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
  const keys = () => spy.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey));
  return { m, keys };
}

let calls: Call[];
const last = () => calls[calls.length - 1];

describe('approval api and mutations', () => {
  it('submit posts to, recordRowVersion, picks, comment with Idempotency-Key', async () => {
    let key: unknown;
    calls = mockHttp((_c, config) => { key = config.headers?.['Idempotency-Key']; return { outcome: 'submitted', instanceId: 3, recordRowVersion: 'v2' }; });
    const { m } = setup();
    await m.submit.mutateAsync({ recordId: 9, moduleKey: 'po', body: { to: 12, recordRowVersion: 'v1', picks: { s1: ['u1'] }, comment: 'hi' }, idempotencyKey: 'k-1' });
    expect(last()).toMatchObject({ method: 'post', url: '/records/9/approval/submit' });
    expect(bodyOf(last())).toEqual({ to: 12, recordRowVersion: 'v1', picks: { s1: ['u1'] }, comment: 'hi' });
    expect(key).toBe('k-1');
  });

  it('approve posts rowVersion, comment and values to the task', async () => {
    calls = mockHttp(() => ({ outcome: 'advanced', instanceId: 3, instanceRowVersion: 'v3' }));
    const { m } = setup();
    await m.approve.mutateAsync({ taskId: 5, recordId: 9, moduleKey: 'po', body: { rowVersion: 'v2', comment: 'ok', values: { f_1: 10 } }, idempotencyKey: 'k' });
    expect(last()).toMatchObject({ method: 'post', url: '/approval-tasks/5/approve' });
    expect(bodyOf(last())).toEqual({ rowVersion: 'v2', comment: 'ok', values: { f_1: 10 } });
  });

  it('reject posts to=previous and comment', async () => {
    calls = mockHttp(() => ({ outcome: 'returned', instanceId: 3, instanceRowVersion: 'v3' }));
    const { m } = setup();
    await m.reject.mutateAsync({ taskId: 5, recordId: 9, moduleKey: 'po', body: { rowVersion: 'v2', comment: 'no', to: 'previous' }, idempotencyKey: 'k' });
    expect(last()).toMatchObject({ method: 'post', url: '/approval-tasks/5/reject' });
    expect(bodyOf(last())).toEqual({ rowVersion: 'v2', comment: 'no', to: 'previous' });
  });

  it('withdraw posts to the instance', async () => {
    calls = mockHttp(() => ({ outcome: 'withdrawn', instanceId: 3, instanceRowVersion: 'v3' }));
    const { m } = setup();
    await m.withdraw.mutateAsync({ instanceId: 3, recordId: 9, moduleKey: 'po', body: { rowVersion: 'v2' }, idempotencyKey: 'k' });
    expect(last()).toMatchObject({ method: 'post', url: '/approval-instances/3/withdraw' });
  });

  it.each(['submit', 'approve', 'reject', 'withdraw'] as const)(
    '%s success invalidates record, approval, history, inbox, bell, list, tasks, effects and ledger', async (which) => {
      calls = mockHttp(() => ({ outcome: 'advanced', instanceId: 3, instanceRowVersion: 'v2', recordRowVersion: 'v2' }));
      const { m, keys } = setup();
      if (which === 'submit') await m.submit.mutateAsync({ recordId: 9, moduleKey: 'po', body: { to: 12, recordRowVersion: 'v1' }, idempotencyKey: 'k' });
      if (which === 'approve') await m.approve.mutateAsync({ taskId: 5, recordId: 9, moduleKey: 'po', body: { rowVersion: 'v1' }, idempotencyKey: 'k' });
      if (which === 'reject') await m.reject.mutateAsync({ taskId: 5, recordId: 9, moduleKey: 'po', body: { rowVersion: 'v1', comment: 'no', to: 'initiator' }, idempotencyKey: 'k' });
      if (which === 'withdraw') await m.withdraw.mutateAsync({ instanceId: 3, recordId: 9, moduleKey: 'po', body: { rowVersion: 'v1' }, idempotencyKey: 'k' });
      await flushPromises();
      expect(keys()).toEqual(expect.arrayContaining([
        '["record",9]', '["record-approval",9]', '["record-history",9]', '["approval-inbox"]', '["notifications"]',
        '["records","po"]', '["record-tasks",9]', '["record-effects",9]', '["record-ledger",9]',
      ]));
    },
  );

  it('inbox passes box, module, status, from, to, currentPage, pagesSize', async () => {
    calls = mockHttp(() => ({ items: [], totalCount: 0, currentPage: 2, pagesSize: 20 }));
    await dynamicApi.getApprovalInbox('submitted', { module: 'po', status: 'rejected', from: '2026-01-01', to: '2026-02-01' }, 2, 20);
    expect(last()).toMatchObject({ method: 'get', url: '/approval-tasks' });
    expect(last().params).toEqual({ box: 'submitted', module: 'po', status: 'rejected', from: '2026-01-01', to: '2026-02-01', currentPage: 2, pagesSize: 20 });
  });

  it('getModuleSchema adds recordId only when given', async () => {
    calls = mockHttp(() => ({}));
    await dynamicApi.getModuleSchema('po');
    expect(last().params).toBeUndefined();
    await dynamicApi.getModuleSchema('po', 7);
    expect(last().params).toEqual({ recordId: 7 });
  });
});
