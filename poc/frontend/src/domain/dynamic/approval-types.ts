import type { I18nText } from './types';

export type ApprovalInstanceStatus = 'pending' | 'approved' | 'rejected' | 'withdrawn' | 'cancelled' | 'rerouted';

/** One in-progress node while two or more different nodes run in parallel; `addSign` marks an added approver on that node. */
export interface ApprovalActiveStage { name: I18nText; addSign: boolean }

/** Summary on GET records/{id}; `none` = never submitted but a submit is possible. */
export interface RecordApprovalSummary {
  status: ApprovalInstanceStatus | 'none';
  stageName: I18nText | null;
  stageSeq: number | null;
  canSubmit: boolean;
  /** Only sent for parallel graph instances and readers who see the whole graph; absent otherwise. */
  activeStages?: ApprovalActiveStage[] | null;
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
  /** 排隊中的加簽關卡的加簽對象姓名（還沒有任務時才有）。 */
  addSignNames?: string[] | null;
  /** Flow graph only (absent for a sequence flow): the node and branch segment this stage run belongs to. */
  nodeId?: string | null;
  branchKey?: string | null;
  /** Flow graph only: why a closed stage was closed. `branchCancelled` = another branch finished an any-join first; `instanceEnded` = the whole request ended. */
  closeReason?: 'branchCancelled' | 'instanceEnded' | null;
}
export interface ApprovalActionView {
  action: string; actorUserId: string; actorName: string; onBehalfOfUserId: string | null; targetUserIds: string[];
  comment: string | null; data: unknown; createdTime: string;
  /** Display names of `targetUserIds` in the same order (transfer, reassign): the log shows "→ name". */
  targetNames?: string[];
  /** Display name of `onBehalfOfUserId`: a delegate acted for that person (design 7.7). */
  onBehalfOfName?: string | null;
  /** The stage run this action belongs to (stage actions only; submit has none). Points to `stages[].id` of the same instance. */
  stageRunId?: number | null;
}
/**
 * Display state of a progress-graph node, computed by the backend (the frontend only maps state to style and text).
 * `changed` / `hidden` appear only for a reader who sees just their own stages.
 */
export type ApprovalProgressState =
  | 'passed' | 'active' | 'returned' | 'skipped' | 'cancelled' | 'ended' | 'notReached' | 'changed' | 'hidden';
/** The seven definition kinds plus `addSign`, a display-only small node attached to a stage (see `hostId`). */
export type ApprovalProgressKind = 'start' | 'approval' | 'condition' | 'fork' | 'join' | 'end' | 'notify' | 'addSign';
export interface ApprovalProgressNode {
  id: string; kind: ApprovalProgressKind; name?: I18nText | null; state: ApprovalProgressState;
  /** `approval` and `addSign` only: points to `stages[].id` of the same instance, oldest first. */
  runIds?: number[];
  /** `join` only: whether all arms or any one arm must arrive. */
  joinMode?: 'all' | 'any';
  /** `addSign` only: the id of the approval node it is attached to. */
  hostId?: string;
}
export interface ApprovalProgressEdge { id: string; from: string; to: string; label?: I18nText | null; traversed: boolean }
export interface ApprovalProgressGraph { nodes: ApprovalProgressNode[]; edges: ApprovalProgressEdge[] }
export interface ApprovalInstanceView {
  id: number; status: ApprovalInstanceStatus; routeKind: 'flow' | 'fallback'; flowName: I18nText | null;
  fromOptionId: number; toOptionId: number; initiatorUserId: string; submittedTime: string; completedTime: string | null;
  rowVersion: string; stages: ApprovalStageView[]; actions: ApprovalActionView[]; canWithdraw: boolean; canCancel: boolean;
  titleSnapshot: string;
  /** Progress graph; absent when the flow snapshot is missing or inconsistent (the timeline still works). */
  graph?: ApprovalProgressGraph | null;
}
export interface RecordApproval { current: ApprovalInstanceView | null; previous: ApprovalInstanceView[]; submitTargets: number[] }

export interface UserRef { userId: string; displayName: string }
export interface PreviewStage {
  code: string | null; name: I18nText; status: 'enter' | 'skip' | 'conditional' | 'pick'; approvers: UserRef[]; usesFallback: boolean;
  /** Designer trial only (absent = no problem): this stage would fail when the request is submitted or reaches it. */
  problem?: 'noApprover' | 'tooManyApprovers' | null;
  /** Flow graph only: the any-join this item belongs to; items sharing a code read as "any one branch finishing is enough". */
  anyGroup?: string | null;
}
export interface PickRequirement { stageCode: string; name: I18nText; min: number; max: number; candidates: UserRef[] }
export interface ApprovalPreview {
  outcome: 'flow' | 'direct' | 'blocked'; flowName: I18nText | null; stages: PreviewStage[]; picks: PickRequirement[];
  /** Designer trial only: the key of the flow that matched (it may be another flow than the one being edited). */
  flowKey?: string | null;
  /** Flow graph only: the nodes and edges the request is expected to pass (absent for a sequence flow). */
  path?: { nodeIds: string[]; edgeIds: string[] } | null;
}

export interface SubmitApprovalBody { to: number; recordRowVersion: string; picks?: Record<string, string[]>; comment?: string }
export interface ApproveBody { rowVersion: string; comment?: string; values?: Record<string, unknown> }
export interface RejectBody { rowVersion: string; comment: string; to: 'initiator' | 'previous' }
export interface AddSignBody { rowVersion: string; userIds: string[]; mode: 'all' | 'any'; comment?: string; values?: Record<string, unknown> }
export interface WithdrawBody { rowVersion: string; comment?: string }
export interface TransferBody { rowVersion: string; userId: string; comment?: string }
/** The caller's own delegate (server: GET me/approval-delegate). Dates are business days `yyyy-MM-dd`, both ends included; `active`: today is inside. */
export interface ApprovalDelegate { delegateUserId: string; delegateName: string; startDate: string; endDate: string; active: boolean; upcoming: boolean }
/** GET me/approval-delegate: today is the server's business day; refetch once secondsToNextDay have passed. No setting: delegate is left out (nulls are not sent). */
export interface MyApprovalDelegate { delegate?: ApprovalDelegate | null; today: string; secondsToNextDay: number }
export interface ApprovalDelegateBody { delegateUserId: string; startDate: string; endDate: string }
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
  /** Same rule as the summary; never sent on the todo tab. */
  activeStages?: ApprovalActiveStage[] | null;
}
export interface ApprovalInboxFilter { module?: string; status?: ApprovalInstanceStatus; from?: string; to?: string }
