import { describe, expect, it } from 'vitest';
import type { ApprovalInstanceView, ApprovalStageView, ApprovalTaskView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import { currentStage, hasApprovalActions, myOpenTask, resultNotice } from './approval-actions';

const task = (p: Partial<ApprovalTaskView> = {}): ApprovalTaskView => ({
  id: 1, assigneeUserId: 'u1', assigneeName: '王', originalUserId: 'u1', reason: 'resolved', status: 'open', actedTime: null, canAct: false, canReturnPrevious: false, ...p,
});
const stage = (p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id: 1, seq: 1, stageCode: 's', name: { 'zh-TW': '主管' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks: [], editableFieldKeys: [], ...p,
});
const inst = (stages: ApprovalStageView[], p: Partial<ApprovalInstanceView> = {}): ApprovalInstanceView => ({
  id: 7, status: 'pending', routeKind: 'flow', flowName: null, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'a', submittedTime: '', completedTime: null,
  rowVersion: 'iv', stages, actions: [], canWithdraw: false, canCancel: false, titleSnapshot: '', ...p,
});
const t = (k: string, p?: Record<string, unknown>) => {
  const msg = k.split('.').reduce<unknown>((o, s) => (o as Record<string, unknown>)?.[s], zhTW) as string;
  return p ? msg.replace(/\{(\w+)\}/g, (_m, n) => String(p[n])) : msg;
};

describe('approval-actions', () => {
  it('currentStage is the active one; myOpenTask is the first task I can act on', () => {
    const i = inst([stage({ id: 1, seq: 1, status: 'approved' }), stage({ id: 2, seq: 2, tasks: [task({ id: 5 }), task({ id: 6, canAct: true }), task({ id: 7, canAct: true })] })]);
    expect(currentStage(i)?.id).toBe(2);
    expect(myOpenTask(i)?.id).toBe(6);
  });

  it('no active stage or nothing to act on: undefined', () => {
    expect(currentStage(inst([stage({ status: 'approved' })]))).toBeUndefined();
    expect(myOpenTask(inst([stage({ tasks: [task()] })]))).toBeUndefined();
  });

  it('hasApprovalActions: approver or the initiator of a pending instance', () => {
    const acting = inst([stage({ tasks: [task({ canAct: true })] })]);
    expect(hasApprovalActions(acting, 'x')).toBe(true);
    expect(hasApprovalActions(inst([stage()]), 'a')).toBe(true);
    expect(hasApprovalActions(inst([stage()]), 'x')).toBe(false);
    expect(hasApprovalActions(inst([stage()], { status: 'approved' }), 'a')).toBe(false);
    expect(hasApprovalActions({ ...inst([stage()]), canCancel: true }, 'x')).toBe(true);   // a manager without a task
    expect(hasApprovalActions(null, 'a')).toBe(false);
  });

  it('resultNotice words each outcome; rerouted says the approval was not counted', () => {
    const r = (outcome: string, extra: object = {}) => resultNotice({ outcome, instanceId: 1, instanceRowVersion: 'v', ...extra } as never, 'zh-TW', t);
    expect(r('advanced').message).toBe('已同意');
    expect(r('approved').message).toBe('已核准');
    expect(r('returned').message).toBe('已退回上一關');
    expect(r('rejected').message).toBe('已退回發起人');
    expect(r('withdrawn').message).toBe('已撤回');
    const flow = r('rerouted', { newFlowName: { 'zh-TW': '大額流程' } });
    expect(flow.message).toBe('單據內容已變，流程改為「大額流程」重新開始，你的同意未計入');
    expect(flow.type).toBe('warning');
    const again = r('rerouted', { newFlowName: { 'zh-TW': '大額流程' }, needsResubmit: true });
    expect(again.message).toBe('單據內容已變，流程改為「大額流程」，需由發起人重新送出；你的同意未計入');
    expect(again.type).toBe('warning');
    expect(r('rerouted', { newFlowName: null }).message).toBe('單據內容已變，改由備援簽核人重新簽核，你的同意未計入');
  });
});
