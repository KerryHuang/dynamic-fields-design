import type { SchemaWorkflow, Workflow } from './types';

// ---- runtime (from the schema; the backend enforces, this only narrows the UI) ----

/**
 * Status values the status field may offer; undefined means no restriction.
 * Create: only the initial state. Edit: the stored state plus what the current user may move to.
 */
export function selectableStates(
  workflow: SchemaWorkflow | undefined, mode: 'create' | 'edit', current: unknown,
): Set<string> | undefined {
  if (!workflow) return undefined;
  if (mode === 'create') return new Set([workflow.initialState]);
  if (typeof current !== 'string' || !current) return undefined;
  return new Set([current, ...workflow.transitions.filter((t) => t.from === current).map((t) => t.to)]);
}

export function isReadOnlyState(workflow: SchemaWorkflow | undefined, state: unknown): boolean {
  return !!workflow && workflow.states.some((s) => s.value === state && s.readOnly);
}

/** Whether the current user can move a record out of `state`. */
export function canAdvance(workflow: SchemaWorkflow | undefined, state: unknown): boolean {
  return !!workflow && workflow.transitions.some((t) => t.from === state);
}

/** Intersection where undefined is "everything". */
export function intersectAllowed(a: Set<string> | undefined, b: Set<string> | undefined): Set<string> | undefined {
  if (!a) return b;
  if (!b) return a;
  return new Set([...a].filter((x) => b.has(x)));
}

// ---- designer draft ----

export interface WorkflowDraft {
  statusFieldId: number | null;
  initialOptionId: number | null;
  states: { optionId: number; readOnly: boolean }[];
  transitions: { from: number | null; to: number | null; groupIds: number[] }[];
}

export const emptyWorkflowDraft = (): WorkflowDraft => ({ statusFieldId: null, initialOptionId: null, states: [], transitions: [] });

export function draftFromWorkflow(w: Workflow | null | undefined): WorkflowDraft {
  return w ? JSON.parse(JSON.stringify(w)) as WorkflowDraft : emptyWorkflowDraft();
}

/** Turning a state off drops the transitions that use it and the initial mark; turning it on adds it. */
export function setStateIncluded(d: WorkflowDraft, optionId: number, included: boolean): WorkflowDraft {
  if (included) {
    return d.states.some((s) => s.optionId === optionId) ? d : { ...d, states: [...d.states, { optionId, readOnly: false }] };
  }
  return {
    ...d,
    initialOptionId: d.initialOptionId === optionId ? null : d.initialOptionId,
    states: d.states.filter((s) => s.optionId !== optionId),
    transitions: d.transitions.filter((t) => t.from !== optionId && t.to !== optionId),
  };
}

export function setStateReadOnly(d: WorkflowDraft, optionId: number, readOnly: boolean): WorkflowDraft {
  return { ...d, states: d.states.map((s) => (s.optionId === optionId ? { ...s, readOnly } : s)) };
}

/** Another status field has other options, so nothing of the old setup carries over. */
export const withStatusField = (d: WorkflowDraft, statusFieldId: number | null): WorkflowDraft =>
  (d.statusFieldId === statusFieldId ? d : { ...emptyWorkflowDraft(), statusFieldId });

/** Request body, or undefined while the draft lacks a status field or an initial state; half-filled transition rows are dropped. */
export function buildWorkflow(d: WorkflowDraft): Workflow | undefined {
  if (d.statusFieldId == null || d.initialOptionId == null) return undefined;
  return {
    statusFieldId: d.statusFieldId,
    initialOptionId: d.initialOptionId,
    states: d.states.map((s) => ({ optionId: s.optionId, readOnly: s.readOnly })),
    transitions: d.transitions.flatMap((t) => (t.from == null || t.to == null ? [] : [{ from: t.from, to: t.to, groupIds: [...t.groupIds] }])),
  };
}
