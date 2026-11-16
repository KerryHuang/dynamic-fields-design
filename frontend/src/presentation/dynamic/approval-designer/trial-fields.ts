import type { ApprovalDefinition, ApprovalFlowInput } from 'src/domain/dynamic/approval-flow-types';
import { isConditionGroup } from 'src/domain/dynamic/condition';
import type { ConditionNode } from 'src/domain/dynamic/types';

function conditionFields(n: ConditionNode | null | undefined, into: Set<number>) {
  if (!n) return;
  if (isConditionGroup(n)) {
    for (const c of 'all' in n ? n.all : n.any) conditionFields(c, into);
    return;
  }
  if (n.field) into.add(n.field);
  const v = n.value as { fieldRef?: number } | null | undefined;
  if (v && typeof v === 'object' && !Array.isArray(v) && v.fieldRef) into.add(v.fieldRef);
}

function definitionFields(d: ApprovalDefinition, into: Set<number>) {
  conditionFields(d.when, into);
  if (d.shape === 'graph') {
    for (const n of d.nodes) {
      conditionFields(n.when, into);
      if (n.approvers?.source === 'userField' && n.approvers.fieldId) into.add(n.approvers.fieldId);
    }
    for (const e of d.edges) conditionFields(e.when, into);
    return;
  }
  for (const s of d.stages) {
    conditionFields(s.when, into);
    if (s.approvers.source === 'userField' && s.approvers.fieldId) into.add(s.approvers.fieldId);
  }
}

/**
 * Fields the trial asks for: the ones that decide which flow matches and who approves, over all flows of the draft's target state
 * (the draft replaces the saved flow of the same key; a draft-only flow has no definition and does not match yet). Fields that are not in the module any more are left to the caller to drop.
 */
export function trialFieldIds(flows: { key: string; toOptionId: number; definition: ApprovalDefinition | null }[], draft: ApprovalFlowInput): number[] {
  const ids = new Set<number>();
  for (const f of flows) if (f.definition && f.toOptionId === draft.toOptionId && f.key !== draft.key) definitionFields(f.definition, ids);
  definitionFields(draft.definition, ids);
  return [...ids];
}
