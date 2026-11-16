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

/** What the package open left behind on a source: names of skipped approver groups (blank ones kept, the export check counts them too), and whether the candidate groups were skipped. */
export function unmatchedOf(source: ApproverSource): { names: string[]; candidates: boolean } {
  return { names: source.unmatchedGroups ?? [], candidates: source.candidatesUnmatched === true };
}

/** Removing the candidate marker of a pick source whose candidates are all gone would let the initiator pick anyone, so it needs candidates first. */
export function removalBlocked(source: ApproverSource): boolean {
  return source.source === 'initiatorPick' && source.candidatesUnmatched === true && !(source.candidateGroupIds?.length);
}

/** Drops the skipped-group markers and keeps every other property of the source. */
export function clearUnmatched(source: ApproverSource): ApproverSource {
  return { ...source, unmatchedGroups: null, candidatesUnmatched: false };
}

export { isSingleApprover } from 'src/domain/dynamic/approval-flow-draft';

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
