import type { ApproverSource, ApproverSourceKind } from 'src/domain/dynamic/approval-flow-types';

/** Policy numbers the editor shows in its hints; undefined = the policy could not be read (no number is shown, the backend still validates). */
export interface ApproverLimits {
  maxApprovers?: number;
  techMaxApprovers?: number;
  maxManagerLevel?: number;
}

type Translate = (key: string, params?: Record<string, unknown>) => string;

export const ALL_SOURCE_KINDS: ApproverSourceKind[] = ['users', 'group', 'manager', 'userField', 'initiatorPick'];
/** A fallback approver may be fixed users, a group, or the initiator's manager (spec 20.4 item 11). */
export const FALLBACK_SOURCE_KINDS: ApproverSourceKind[] = ['users', 'group', 'manager'];

/** Params a freshly chosen source starts with; the manager level and pick range are the smallest valid values, not policy numbers. */
export function defaultSourceOf(kind: ApproverSourceKind): ApproverSource {
  switch (kind) {
    case 'users': return { source: 'users', userIds: [] };
    case 'group': return { source: 'group', groupIds: [] };
    case 'manager': return { source: 'manager', level: 1 };
    case 'userField': return { source: 'userField', fieldId: null, candidateGroupIds: [] };
    case 'initiatorPick': return { source: 'initiatorPick', candidateGroupIds: [], min: 1, max: 1 };
  }
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

/** Manager level choices 1..max; a saved level above max stays selectable and is marked (tightening only governs what comes after). */
export function levelOptions(max: number | undefined, saved?: number | null): { value: number; legacy: boolean }[] {
  if (max === undefined) return [];
  const top = Math.max(max, saved ?? 0);
  return Array.from({ length: top }, (_, i) => ({ value: i + 1, legacy: i + 1 > max }));
}

/** The explanation under the parameters; undefined when a number it needs is unknown. */
export function hintOf(kind: ApproverSourceKind, limits: ApproverLimits, t: Translate, fallback = false): string | undefined {
  const { maxApprovers: max, techMaxApprovers: tech } = limits;
  if (fallback) return tech === undefined ? undefined : t('approval.designer.source.hint.fallback', { tech });
  switch (kind) {
    case 'users': return max === undefined || tech === undefined ? undefined : t('approval.designer.source.hint.users', { max, tech });
    case 'initiatorPick': return max === undefined ? undefined : t('approval.designer.source.hint.pick', { max });
    default: return max === undefined ? undefined : t('approval.designer.source.hint.group', { max });
  }
}
