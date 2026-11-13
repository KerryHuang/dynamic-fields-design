import type { ApprovalDefinition, ApprovalFlow, ApprovalFlowInput, ApproverSource } from './approval-flow-types';
import { joinList } from 'src/i18n/translate';
import { pruneCondition } from './condition';
import type { ConditionNode, I18nText } from './types';

export interface StageDraft {
  code: string;
  name: I18nText;
  when?: ConditionNode;
  approvers: ApproverSource;
  mode: 'all' | 'any';
  editableFields: number[];
  dueHours: number | null;
  onEmpty: 'fallback' | 'skip';
  /** The code this stage was saved with (absent on a new stage); changing it makes the backend treat the stage as a new one. Not sent. */
  savedCode?: string;
  /** The mode the designer chose while the source was a single person (which forces `any`); restored when the source stops being one. Not sent. */
  chosenMode?: 'all' | 'any';
  /** Stable key for the stage card while the list is edited (not sent). */
  uid?: string;
}

export interface FlowDraft {
  key: string;
  name: I18nText;
  toOptionId: number | null;
  priority: number;
  active: boolean;
  when?: ConditionNode;
  rejectTo: number | null;
  /** null = inherit the module's default fallback approvers (policy). */
  fallback: ApproverSource | null;
  stages: StageDraft[];
}

/** Screen default only (not a business policy): the prefix of a pre-filled stage code. */
const CODE_PREFIX = 'stage-';

let uidSeq = 0;
const nextUid = () => `s${++uidSeq}`;

/** The smallest `stage-N` (N from 1) not already used; the code never follows the name. */
export function newStage(existingCodes: string[]): StageDraft {
  const used = new Set(existingCodes);
  let n = 1;
  while (used.has(`${CODE_PREFIX}${n}`)) n++;
  return {
    code: `${CODE_PREFIX}${n}`, name: {}, approvers: { source: 'manager', level: 1 }, mode: 'any',
    editableFields: [], dueHours: null, onEmpty: 'fallback', uid: nextUid(),
  };
}

export function emptyFlowDraft(toOptionId: number | null, priority: number): FlowDraft {
  return { key: '', name: {}, toOptionId, priority, active: true, rejectTo: null, fallback: null, stages: [newStage([])] };
}

export function draftFromFlow(f: ApprovalFlow): FlowDraft {
  const d = f.definition;
  return {
    key: f.key, name: { ...f.name }, toOptionId: f.toOptionId, priority: f.priority, active: f.active,
    when: d.when ?? undefined, rejectTo: d.rejectTo ?? null, fallback: d.fallbackApprovers ?? null,
    stages: d.stages.map((s) => ({
      code: s.code, name: { ...s.name }, when: s.when ?? undefined, approvers: s.approvers, mode: s.mode,
      editableFields: [...s.editableFields], dueHours: s.dueHours ?? null, onEmpty: s.onEmpty, savedCode: s.code, uid: nextUid(),
    })),
  };
}

type HasStages = { stages: StageDraft[] };

/** `reserved`: codes of the saved version; the backend matches a stage to the saved one by its current code, so a new stage must not take one of them. */
export function insertStage<T extends HasStages>(d: T, at: number, reserved: string[] = []): T {
  const stages = [...d.stages];
  stages.splice(Math.max(0, Math.min(at, stages.length)), 0, newStage([...stages.map((s) => s.code), ...reserved]));
  return { ...d, stages };
}

export function moveStage<T extends HasStages>(d: T, i: number, delta: -1 | 1): T {
  const j = i + delta;
  if (i < 0 || i >= d.stages.length || j < 0 || j >= d.stages.length) return d;
  const stages = [...d.stages];
  [stages[i], stages[j]] = [stages[j]!, stages[i]!];
  return { ...d, stages };
}

/** The last stage stays: a flow needs at least one. */
export function removeStage<T extends HasStages>(d: T, i: number): T {
  if (d.stages.length <= 1 || i < 0 || i >= d.stages.length) return d;
  return { ...d, stages: d.stages.filter((_, k) => k !== i) };
}

/** One person at most can ever come out of this source, so the stage mode does not matter (saved as `any`). */
export function isSingleApprover(s: ApproverSource): boolean {
  switch (s.source) {
    case 'manager':
    case 'userField': return true;
    case 'users': return (s.userIds?.length ?? 0) <= 1;
    case 'initiatorPick': return s.max === 1;
    default: return false;
  }
}

/** Null while no target state is chosen. Empty conditions become null. A single-person source gets mode `any` when it is chosen (the stage editor does that); a saved stage keeps the mode it came with so an untouched save stays the same version. */
export function buildFlowInput(d: FlowDraft): ApprovalFlowInput | null {
  if (d.toOptionId === null) return null;
  const definition: ApprovalDefinition = {
    shape: 'sequence',
    when: pruneCondition(d.when) ?? null,
    rejectTo: d.rejectTo,
    fallbackApprovers: d.fallback,
    stages: d.stages.map((s) => ({
      code: s.code, name: s.name, when: pruneCondition(s.when) ?? null, approvers: s.approvers,
      mode: s.mode, editableFields: s.editableFields, dueHours: s.dueHours, onEmpty: s.onEmpty,
    })),
  };
  return { key: d.key, name: d.name, toOptionId: d.toOptionId, priority: d.priority, active: d.active, definition };
}

/** Same comparison as the backend: key order and null vs missing do not count. */
function sameValue(a: unknown, b: unknown): boolean {
  if (a === undefined || a === null) return b === undefined || b === null;
  if (typeof a !== 'object' || b === null || typeof b !== 'object') return a === b;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const x = a as Record<string, unknown>;
  const y = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(x), ...Object.keys(y)]);
  for (const k of keys) if (!sameValue(x[k], y[k])) return false;
  return true;
}

/** True when saving the draft would change the saved flow's definition (only then does a used version turn into a new one). */
export function definitionChanged(d: FlowDraft, saved: ApprovalFlow): boolean {
  const built = buildFlowInput(d);
  return !built || !sameValue(built.definition, saved.definition);
}

/** Last in the target state (max + 1), 1 when it has none; `exceptKey` leaves out the flow being edited. */
export function nextPriority(flows: ApprovalFlow[], toOptionId: number, exceptKey?: string): number {
  const mine = flows.filter((f) => f.toOptionId === toOptionId && f.key !== exceptKey);
  return mine.length ? Math.max(...mine.map((f) => f.priority)) + 1 : 1;
}

/** Messages of `definition.stages[i].<field>` keyed by field (code, name, when, approvers, mode, editableFields, dueHours, onEmpty); a key without a field lands on `_`. */
export function stageErrors(errors: Record<string, string[]> | undefined, i: number): Record<string, string> {
  const out: Record<string, string[]> = {};
  const prefix = `definition.stages[${i}]`;
  for (const [k, v] of Object.entries(errors ?? {})) {
    if (k !== prefix && !k.startsWith(`${prefix}.`) && !k.startsWith(`${prefix}[`)) continue;
    const field = k.slice(prefix.length).replace(/^\./, '').split(/[.[]/)[0] || '_';
    (out[field] ??= []).push(...v);
  }
  return Object.fromEntries(Object.entries(out).map(([k, v]) => [k, joinList(v)]));
}
