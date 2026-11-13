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

export interface ApprovalDefinition {
  shape: 'sequence';
  when?: ConditionNode | null;
  rejectTo?: number | null;
  fallbackApprovers?: ApproverSource | null;
  stages: ApprovalStageDef[];
}

export interface ApprovalFlowInput {
  key: string;
  name: I18nText;
  toOptionId: number;
  priority: number;
  active: boolean;
  definition: ApprovalDefinition;
}

export interface ApprovalFlow extends ApprovalFlowInput {
  moduleKey: string;
  version: number;
  used: boolean;
  pendingCount: number;
  /** Whether the current version is bound to an instance (a4a1); absent on an older backend. */
  currentVersionUsed?: boolean;
}
