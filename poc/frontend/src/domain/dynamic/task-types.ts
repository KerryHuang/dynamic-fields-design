import type { I18nText } from './types';

export type TaskStatus = 'todo' | 'inProgress' | 'done' | 'cancelled';
export type TaskListTab = 'assigned' | 'created' | 'mentioned';
export type TaskListFilter = 'open' | 'overdue' | 'today' | 'thisWeek' | 'all';

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
  repeat: 'once' | 'everyTime';
  /** once: already applied. everyTime never sets this. */
  applied: boolean;
  applicable: boolean;
  /** everyTime: the previous round still has open tasks. */
  previousOpen: boolean;
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

/** Quasar brand color for a status badge. */
export const taskStatusColor = (s: TaskStatus): string => (s === 'done' ? 'positive' : s === 'cancelled' ? 'muted' : 'primary');

/** Kinds the server writes today (NotificationKinds); an unknown kind still renders, with its title only. */
export const NOTIFICATION_KINDS = [
  'taskAssigned', 'taskMentioned', 'taskCommented', 'taskDueSoon', 'taskOverdue', 'taskCompleted', 'taskCancelled',
] as const;

export interface NotificationItem {
  id: number;
  kind: string;
  /** Task title at the time the notification was written. */
  title: string;
  /** In-app path such as `/tasks?open=5`; empty when there is nothing to open. */
  link: string;
  read: boolean;
  createdTime: string;
}

export interface NotificationList {
  items: NotificationItem[];
  unreadCount: number;
}
