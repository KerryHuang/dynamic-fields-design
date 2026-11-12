import type {
  ApprovalActionResult, ApprovalInstanceView, ApprovalStageView, ApprovalTaskView,
} from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';

type Translate = (key: string, params?: Record<string, unknown>) => string;

export const currentStage = (i: ApprovalInstanceView): ApprovalStageView | undefined => i.stages.find((s) => s.status === 'active');

/** The first task of the current stage the signed-in user may act on (the server decides `canAct`). */
export const myOpenTask = (i: ApprovalInstanceView): ApprovalTaskView | undefined => currentStage(i)?.tasks.find((k) => k.canAct);

/** Whether the bar has anything to show: an approver with an open task, or the initiator of a running approval. */
export function hasApprovalActions(i: ApprovalInstanceView | null, userId: string): boolean {
  if (!i || i.status !== 'pending') return false;
  return !!myOpenTask(i) || (!!userId && i.initiatorUserId === userId);
}

/** task is missing for someone who is not an assignee (an administrator): extensions must cope with that. */
export interface ApprovalActionContext { instance: ApprovalInstanceView; task?: ApprovalTaskView; recordId: number; refresh: () => void }
export interface ApprovalExtraAction {
  key: string;
  label: (t: Translate) => string;
  visible: (c: ApprovalActionContext) => boolean;
  run: (c: ApprovalActionContext) => void;
}
/** Extension point: transfer, add-sign, cancel and reassign (a3) register here and show up in the "more" menu. */
export const approvalTaskActions: ApprovalExtraAction[] = [];

export interface ActionNotice { type: 'positive' | 'warning'; message: string }
/** What to tell the user after an approve, reject or withdraw succeeded. */
export function resultNotice(r: ApprovalActionResult, locale: string, t: Translate): ActionNotice {
  if (r.outcome === 'rerouted') {
    const flow = r.newFlowName ? resolveI18n(r.newFlowName, locale) : '';
    if (r.needsResubmit) return { type: 'warning', message: t('approval.result.reroutedResubmit', { flow }) };
    return { type: 'warning', message: flow ? t('approval.result.reroutedFlow', { flow }) : t('approval.result.reroutedFallback') };
  }
  return { type: 'positive', message: t(`approval.result.${r.outcome}`) };
}
