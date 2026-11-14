import type { I18nText } from './types';

export type ApprovalInstanceStatus = 'pending' | 'approved' | 'rejected' | 'withdrawn' | 'cancelled' | 'rerouted';

/** Summary on GET records/{id}; `none` = never submitted but a submit is possible. */
export interface RecordApprovalSummary {
  status: ApprovalInstanceStatus | 'none';
  stageName: I18nText | null;
  stageSeq: number | null;
  canSubmit: boolean;
}

export interface RecordApprovalLock { fieldKeys: string[]; detailRowsLocked: boolean }

export interface ApprovalTaskView {
  id: number; assigneeUserId: string; assigneeName: string; originalUserId: string; reason: string;
  status: string; actedTime: string | null; canAct: boolean;
  /** Server rule: the previous stage in the flow definition (skipped ones passed over) was approved. */
  canReturnPrevious: boolean;
  /** The first holder of the seat when it was handed over (transfer): shown as "original approver". Null when the holder is the original. */
  originalName?: string | null;
  /** Server rule: can act, and the instance has not reached the add-sign stage limit (policy `approval.maxAddSignStages`). */
  canAddSign?: boolean;
}
export interface ApprovalStageView {
  id: number; seq: number; stageCode: string | null; name: I18nText; mode: 'all' | 'any'; status: string;
  skipReason: string | null; dueTime: string | null; tasks: ApprovalTaskView[]; editableFieldKeys: string[];
  /** Display name of whoever added this stage (add-sign stages have `stageCode` null); null for flow stages. */
  addedByName?: string | null;
}
export interface ApprovalActionView {
  action: string; actorUserId: string; actorName: string; onBehalfOfUserId: string | null; targetUserIds: string[];
  comment: string | null; data: unknown; createdTime: string;
  /** Display names of `targetUserIds` in the same order (transfer, reassign): the log shows "→ name". */
  targetNames?: string[];
}
export interface ApprovalInstanceView {
  id: number; status: ApprovalInstanceStatus; routeKind: 'flow' | 'fallback'; flowName: I18nText | null;
  fromOptionId: number; toOptionId: number; initiatorUserId: string; submittedTime: string; completedTime: string | null;
  rowVersion: string; stages: ApprovalStageView[]; actions: ApprovalActionView[]; canWithdraw: boolean; canCancel: boolean;
  titleSnapshot: string;
}
export interface RecordApproval { current: ApprovalInstanceView | null; previous: ApprovalInstanceView[]; submitTargets: number[] }

export interface UserRef { userId: string; displayName: string }
export interface PreviewStage {
  code: string | null; name: I18nText; status: 'enter' | 'skip' | 'conditional' | 'pick'; approvers: UserRef[]; usesFallback: boolean;
  /** Designer trial only (absent = no problem): this stage would fail when the request is submitted or reaches it. */
  problem?: 'noApprover' | 'tooManyApprovers' | null;
}
export interface PickRequirement { stageCode: string; name: I18nText; min: number; max: number; candidates: UserRef[] }
export interface ApprovalPreview {
  outcome: 'flow' | 'direct' | 'blocked'; flowName: I18nText | null; stages: PreviewStage[]; picks: PickRequirement[];
  /** Designer trial only: the key of the flow that matched (it may be another flow than the one being edited). */
  flowKey?: string | null;
}

export interface SubmitApprovalBody { to: number; recordRowVersion: string; picks?: Record<string, string[]>; comment?: string }
export interface ApproveBody { rowVersion: string; comment?: string; values?: Record<string, unknown> }
export interface RejectBody { rowVersion: string; comment: string; to: 'initiator' | 'previous' }
export interface AddSignBody { rowVersion: string; userIds: string[]; mode: 'all' | 'any'; comment?: string; values?: Record<string, unknown> }
export interface WithdrawBody { rowVersion: string; comment?: string }
export interface TransferBody { rowVersion: string; userId: string; comment?: string }
export interface CancelBody { rowVersion: string; comment: string }
export interface ReassignBody { rowVersion: string; userId: string; comment?: string }

export interface SubmitApprovalResult { outcome: 'submitted' | 'approved' | 'direct'; instanceId: number | null; recordRowVersion: string }
export interface ApprovalActionResult {
  outcome: 'advanced' | 'approved' | 'rerouted' | 'rejected' | 'returned' | 'withdrawn' | 'transferred' | 'reassigned' | 'cancelled';
  instanceId: number; instanceRowVersion: string; newFlowName?: I18nText | null;
  /** rerouted into a flow that needs the initiator's picks: no new instance, the initiator has to submit again. */
  needsResubmit?: boolean;
}

export type ApprovalBox = 'todo' | 'done' | 'submitted' | 'all';
export interface ApprovalInboxItem {
  taskId: number; instanceId: number; recordId: number; moduleKey: string; moduleName: I18nText; titleSnapshot: string;
  stageName: I18nText | null; initiatorUserId: string; initiatorName: string; submittedTime: string; dueTime: string | null;
  overdue: boolean; moduleClosed: boolean; status: ApprovalInstanceStatus;
}
export interface ApprovalInboxFilter { module?: string; status?: ApprovalInstanceStatus; from?: string; to?: string }
