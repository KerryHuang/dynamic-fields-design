import { describe, expect, it } from 'vitest';
import type { ApprovalInstanceView, ApprovalTaskView } from 'src/domain/dynamic/approval-types';
import { targetCandidates } from './approval-targets';

const task = (p: Partial<ApprovalTaskView>): ApprovalTaskView => ({
  id: 1, assigneeUserId: 'me', assigneeName: '我', originalUserId: 'me', reason: 'resolved', status: 'open', actedTime: null, canAct: false, canReturnPrevious: false, ...p,
});
const inst = (tasks: ApprovalTaskView[]): ApprovalInstanceView => ({
  id: 7, status: 'pending', routeKind: 'flow', flowName: null, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'init', submittedTime: '', completedTime: null,
  rowVersion: 'iv', stages: [{ id: 1, seq: 1, stageCode: 's', name: {}, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks, editableFieldKeys: [] }],
  actions: [], canWithdraw: false, canCancel: false, titleSnapshot: '',
});
const users = ['me', 'init', 'peer', 'away', 'delegate', 'boss', 'back'].map((userId) => ({ userId, displayName: userId.toUpperCase() }));
const values = (o: { value: string }[]) => o.map((x) => x.value);

describe('targetCandidates', () => {
  it('excludes me, the initiator and seat holders but keeps transferred-out holders', () => {
    const i = inst([
      task({ id: 1, assigneeUserId: 'me', canAct: true }), task({ id: 2, assigneeUserId: 'peer' }),
      task({ id: 3, assigneeUserId: 'away', status: 'transferred' }), task({ id: 4, assigneeUserId: 'boss', status: 'approved' }),
      task({ id: 5, assigneeUserId: 'delegate', originalUserId: 'back', reason: 'delegated' }),
    ]);
    expect(values(targetCandidates(users, i, 'me', false))).toEqual(['away']);
  });

  it('keeps the initiator when allowed', () => {
    const i = inst([task({ canAct: true })]);
    expect(values(targetCandidates(users, i, 'me', true))).toContain('init');
    expect(values(targetCandidates(users, i, 'me', false))).not.toContain('init');
  });

  it('labels with the display name', () => {
    expect(targetCandidates(users, inst([task({})]), 'me', true)[0]).toEqual({ value: 'init', label: 'INIT' });
  });
});
