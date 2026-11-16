import type { ConditionNode, I18nText } from './types';

/** Designer-side approval flow (GET/POST/PUT approval-flows); camelCase mirror of the backend records. */
export type ApproverSourceKind = 'users' | 'group' | 'manager' | 'userField' | 'initiatorPick';

export interface ApproverSource {
  source: ApproverSourceKind;
  userIds?: string[] | null;
  groupIds?: number[] | null;
  level?: number | null;
  fieldId?: number | null;
  candidateGroupIds?: number[] | null;
  min?: number | null;
  max?: number | null;
  unmatchedGroups?: string[] | null;
  candidatesUnmatched?: boolean;
}

export interface ApprovalStageDef {
  code: string;
  name: I18nText;
  when?: ConditionNode | null;
  approvers: ApproverSource;
  mode: 'all' | 'any';
  editableFields: number[];
  dueHours?: number | null;
  onEmpty: 'fallback' | 'skip';
}

export interface SequenceDefinition {
  shape: 'sequence';
  when?: ConditionNode | null;
  rejectTo?: number | null;
  fallbackApprovers?: ApproverSource | null;
  stages: ApprovalStageDef[];
}

export type ApprovalNodeKind = 'start' | 'approval' | 'condition' | 'fork' | 'join' | 'end' | 'notify';

/** A graph node; which properties apply depends on `kind` (backend `ApprovalNodeDef`, nulls omitted). `mode`: approval = all/any, join = all/any arrival. */
export interface ApprovalNodeDef {
  id: string;
  kind: ApprovalNodeKind;
  name?: I18nText | null;
  when?: ConditionNode | null;
  approvers?: ApproverSource | null;
  mode?: 'all' | 'any' | null;
  editableFields?: number[] | null;
  dueHours?: number | null;
  onEmpty?: 'fallback' | 'skip' | null;
  forkId?: string | null;
  recipients?: unknown[] | null;
  template?: unknown | null;
}

/** `when`, `order` and `default` only apply to out edges of a condition node. */
export interface ApprovalEdgeDef {
  id: string;
  from: string;
  to: string;
  label?: I18nText | null;
  when?: ConditionNode | null;
  order?: number | null;
  default?: boolean | null;
}

export interface GraphDefinition {
  shape: 'graph';
  when?: ConditionNode | null;
  rejectTo?: number | null;
  fallbackApprovers?: ApproverSource | null;
  /** Always empty on a graph (the backend writes `[]`). */
  stages: ApprovalStageDef[];
  nodes: ApprovalNodeDef[];
  edges: ApprovalEdgeDef[];
}

export type ApprovalDefinition = SequenceDefinition | GraphDefinition;

/** Node positions saved with a version or draft (canvas viewport is not saved). */
export interface ApprovalFlowLayout {
  nodes: Record<string, { x: number; y: number }>;
}

export interface ApprovalFlowDraft {
  definition: GraphDefinition;
  layout?: ApprovalFlowLayout | null;
}

/** Same key format as the errors (`definition.nodes.<id>…`). */
export interface ApprovalFlowWarning {
  key: string;
  code: string;
  message: string;
}

/** Dry-run validation (`POST approval-flows/validate`): the same checks as publish, nothing is written. */
export interface ApprovalFlowValidation {
  errors: Record<string, string[]>;
  warnings: ApprovalFlowWarning[];
  newVersion: boolean;
  nextVersion: number | null;
  pendingCount: number;
}

/** Body of draft create / save, publish and validate. `baseRev` / `baseVersionId` are what the editor loaded (optimistic lock); `force` overwrites. */
export interface ApprovalFlowDraftInput {
  key: string;
  name: I18nText;
  toOptionId: number;
  priority: number;
  definition: GraphDefinition;
  layout?: ApprovalFlowLayout | null;
  baseRev?: string | null;
  baseVersionId?: number | null;
  force?: boolean;
}

export interface ApprovalFlowPublishResult {
  flow: ApprovalFlow;
  warnings: ApprovalFlowWarning[];
}

export interface ApprovalFlowInput {
  key: string;
  name: I18nText;
  toOptionId: number;
  priority: number;
  active: boolean;
  definition: ApprovalDefinition;
}

export interface ApprovalFlow extends Omit<ApprovalFlowInput, 'definition'> {
  moduleKey: string;
  /** null = never published (a draft-only flow). */
  version: number | null;
  definition: ApprovalDefinition | null;
  /** Single GET only: a sequence flow converted to a graph (what the designer edits). */
  graph?: GraphDefinition | null;
  /** Layout of the current version. */
  layout?: ApprovalFlowLayout | null;
  draft?: ApprovalFlowDraft | null;
  /** Random token of the stored draft; null when there is none or it cannot be read. */
  draftRev?: string | null;
  draftUpdatedBy?: string | null;
  draftUpdatedByName?: string | null;
  draftUpdatedTime?: string | null;
  used: boolean;
  pendingCount: number;
  /** Whether the current version is bound to an instance (a4a1); absent on an older backend. */
  currentVersionUsed?: boolean;
}

/** A published flow in the sequence shape: the only kind the old dialog can edit. */
export type SequenceFlow = ApprovalFlow & { version: number; definition: SequenceDefinition };

/** Trial of the flow being edited (not saved): `values` override the record's, keyed `f_{fieldId}`. */
export interface ApprovalDraftPreviewBody {
  draft: ApprovalFlowInput;
  initiatorUserId: string;
  recordId?: number;
  values?: Record<string, unknown>;
}
