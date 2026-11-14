import type { SelectOption } from 'src/components/framework/GenericSelect.vue';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import type { User } from 'src/domain/dynamic/types';
import { currentStage } from './approval-actions';

/**
 * Who a transfer (or reassign, add-sign) may go to: active users except me, the initiator (unless the self-approval policy allows it)
 * and anyone who still holds a vote in the current stage (a task that is not `transferred`/`reassigned`, or the original behind a delegated task).
 * Only a convenience: the server checks the same rules again.
 */
export function targetCandidates(users: User[], instance: ApprovalInstanceView, me: string, allowInitiator: boolean): SelectOption[] {
  const holders = new Set<string>();
  for (const k of currentStage(instance)?.tasks ?? []) {
    if (k.status === 'transferred' || k.status === 'reassigned') continue;
    holders.add(k.assigneeUserId);
    if (k.reason === 'delegated') holders.add(k.originalUserId);
  }
  return users
    .filter((u) => u.userId !== me && !holders.has(u.userId) && (allowInitiator || u.userId !== instance.initiatorUserId))
    .map((u) => ({ value: u.userId, label: u.displayName || u.userId }));
}
