import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { Workflow } from 'src/domain/dynamic/types';

export interface FlowGroup {
  toOptionId: number;
  label: string;
  /** Approval-required transitions into this state, as "from → to" text. */
  entries: string[];
  hasApprovalEntry: boolean;
  flows: ApprovalFlow[];
}

/**
 * Flows grouped by target state, in the workflow's state order (targets the workflow no longer lists come last), each group by priority.
 * A state with an approval entry but no flow yet still gets a group, so the designer sees what is missing.
 */
export function groupFlows(flows: ApprovalFlow[], workflow: Workflow | undefined, labelOf: (optionId: number) => string): FlowGroup[] {
  const order = (workflow?.states ?? []).map((s) => s.optionId);
  const targets = [...new Set([...order, ...flows.map((f) => f.toOptionId)])];
  const rank = (id: number) => { const i = order.indexOf(id); return i < 0 ? order.length : i; };
  return targets
    .map((toOptionId) => {
      const entries = (workflow?.transitions ?? []).filter((tr) => tr.to === toOptionId && tr.approval?.required)
        .map((tr) => `${labelOf(tr.from)} → ${labelOf(tr.to)}`);
      return {
        toOptionId, label: labelOf(toOptionId), entries, hasApprovalEntry: entries.length > 0,
        flows: flows.filter((f) => f.toOptionId === toOptionId).sort((a, b) => a.priority - b.priority),
      };
    })
    .filter((g) => g.flows.length > 0 || g.hasApprovalEntry)
    .sort((a, b) => rank(a.toOptionId) - rank(b.toOptionId));
}

/** Swaps `key` with its neighbour inside the group's key list; null when it is already at that end (or absent). */
export function moveKeys(keys: string[], key: string, dir: -1 | 1): string[] | null {
  const i = keys.indexOf(key);
  const j = i + dir;
  if (i < 0 || j < 0 || j >= keys.length) return null;
  const next = [...keys];
  [next[i], next[j]] = [next[j]!, next[i]!];
  return next;
}

/** Moves `key` to where `target` sits (drag and drop); null when nothing changes. */
export function moveKeyTo(keys: string[], key: string, target: string): string[] | null {
  const from = keys.indexOf(key);
  const to = keys.indexOf(target);
  if (from < 0 || to < 0 || from === to) return null;
  const next = keys.filter((k) => k !== key);
  next.splice(to, 0, key);
  return next;
}
