import type { I18nText } from './types';

export type TaskStatus = 'todo' | 'inProgress' | 'done' | 'cancelled';
export type TaskListTab = 'assigned' | 'created' | 'mentioned';
export type TaskListFilter = 'open' | 'overdue' | 'thisWeek' | 'all';

export interface TaskRecordRef {
  accessible: boolean;
  moduleKey: string | null;
  recordId: number | null;
  title: string | null;
  deleted: boolean;
}

export interface TaskSummary {
  id: number;
  title: string;
  status: TaskStatus;
  assigneeUserId: string | null;
  assigneeName: string | null;
  assigneeActive: boolean;
  dueDate: string | null;
  overdue: boolean;
  record: TaskRecordRef | null;
  fromRule: boolean;
  canEdit: boolean;
}

export interface TaskDetail extends TaskSummary {
  description: string | null;
  createdBy: string | null;
  createdByName: string | null;
  createdTime: string;
  completedTime: string | null;
  ruleKey: string | null;
  ruleDeleted: boolean;
  canComment: boolean;
  canDelete: boolean;
  frozen: boolean;
}

export interface ManualRule {
  key: string;
  name: I18nText;
  applied: boolean;
  applicable: boolean;
}

export interface RecordTasks {
  items: TaskSummary[];
  canAdd: boolean;
  manualRules: ManualRule[];
}

export interface MentionRef {
  userId: string;
  displayName: string;
  active: boolean;
}

export interface TaskAttachment {
  id: number;
  fileName: string;
  contentType: string;
  size: number;
}

export interface TaskComment {
  id: number;
  kind: 'comment' | 'system';
  authorUserId: string | null;
  authorName: string | null;
  /** Absent on the wire for a withdrawn comment; normalized to null. */
  body: string | null;
  withdrawn: boolean;
  systemEvent: string | null;
  systemData: { from?: unknown; to?: unknown } | null;
  createdTime: string;
  mentions: MentionRef[];
  attachments: TaskAttachment[];
  canWithdraw: boolean;
}

export type TaskPatch = {
  title?: string;
  description?: string | null;
  assigneeUserId?: string | null;
  dueDate?: string | null;
  status?: TaskStatus;
};

export interface TaskInput {
  title: string;
  description?: string | null;
  assigneeUserId?: string | null;
  dueDate?: string | null;
}

export interface TaskPage {
  items: TaskSummary[];
  totalCount: number;
  currentPage: number;
  pagesSize: number;
}

export interface TaskCounts {
  dueToday: number;
  overdue: number;
}
