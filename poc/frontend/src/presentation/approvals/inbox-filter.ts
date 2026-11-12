import type { ApprovalInstanceStatus } from 'src/domain/dynamic/approval-types';

export interface ModuleOption { value: string; label: string }

/** Inbox statuses the server accepts (J7). */
export const INBOX_STATUSES: ApprovalInstanceStatus[] = ['pending', 'approved', 'rejected', 'withdrawn', 'cancelled', 'rerouted'];
