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

export interface ActionNotice { type: 'positive' | 'warning'; message: string }
/** What to tell the user after an approve, reject, transfer or withdraw succeeded. */
export function resultNotice(r: ApprovalActionResult, locale: string, t: Translate): ActionNotice {
  if (r.outcome === 'rerouted') {
    const flow = r.newFlowName ? resolveI18n(r.newFlowName, locale) : '';
    if (r.needsResubmit) return { type: 'warning', message: t('approval.result.reroutedResubmit', { flow }) };
    return { type: 'warning', message: flow ? t('approval.result.reroutedFlow', { flow }) : t('approval.result.reroutedFallback') };
  }
  return { type: 'positive', message: t(`approval.result.${r.outcome}`) };
}

const sameValue = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
/** Only the keys whose value differs from the one the record had; unchanged ones are not sent (the server judges what is sent). */
export function changedValues(keys: string[], initial: Record<string, unknown>, current: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(keys.filter((k) => !sameValue(initial[k], current[k])).map((k) => [k, current[k] ?? null]));
}
