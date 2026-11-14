import type {
  ManualRule, NotificationItem, NotificationList, RecordTasks, TaskCapabilities, TaskComment, TaskDetail, TaskPage, TaskRecordRef, TaskSummary,
  TaskUpdateResult,
} from 'src/domain/dynamic/task-types';
import type { TaskRule } from 'src/domain/dynamic/task-rule-draft';

/** The server leaves out every property whose value is null; the types promise `null`, so put it back. */
type Wire<T> = { [K in keyof T]?: T[K] | null };

const normalizeRecord = (r: Wire<TaskRecordRef> | null | undefined): TaskRecordRef | null => (r ? {
  accessible: !!r.accessible,
  moduleKey: r.moduleKey ?? null,
  recordId: r.recordId ?? null,
  title: r.title ?? null,
  deleted: !!r.deleted,
} : null);

export function normalizeTaskSummary(t: Wire<TaskSummary>): TaskSummary {
  return {
    id: t.id as number,
    title: t.title ?? '',
    status: t.status ?? 'todo',
    assigneeUserId: t.assigneeUserId ?? null,
    assigneeName: t.assigneeName ?? null,
    assigneeActive: !!t.assigneeActive,
    dueDate: t.dueDate ?? null,
    overdue: !!t.overdue,
    record: normalizeRecord(t.record),
    fromRule: !!t.fromRule,
    canEdit: !!t.canEdit,
    canReassign: !!t.canReassign,
    supervised: !!t.supervised,
    version: t.version ?? '',
  };
}

export function normalizeTaskDetail(t: Wire<TaskDetail>): TaskDetail {
  return {
    ...normalizeTaskSummary(t),
    description: t.description ?? null,
    createdBy: t.createdBy ?? null,
    createdByName: t.createdByName ?? null,
    createdTime: t.createdTime ?? '',
    completedTime: t.completedTime ?? null,
    ruleKey: t.ruleKey ?? null,
    ruleDeleted: !!t.ruleDeleted,
    canComment: !!t.canComment,
    canDelete: !!t.canDelete,
    frozen: !!t.frozen,
  };
}

/** `{ id, visible: false, undoToken }` carries no task content; anything else is the task detail. */
export function normalizeTaskUpdate(r: Wire<TaskDetail> & { visible?: boolean; undoToken?: string | null }): TaskUpdateResult {
  if (r.visible === false) return { id: r.id as number, visible: false, undoToken: r.undoToken ?? null };
  return normalizeTaskDetail(r);
}

export const normalizeTaskCapabilities = (c: Partial<TaskCapabilities> | null | undefined): TaskCapabilities => ({ subordinates: !!c?.subordinates });

export function normalizeTaskPage(p: Partial<Omit<TaskPage, 'items'>> & { items?: Wire<TaskSummary>[] }): TaskPage {
  return {
    items: (p.items ?? []).map(normalizeTaskSummary),
    totalCount: p.totalCount ?? 0,
    currentPage: p.currentPage ?? 1,
    pagesSize: p.pagesSize ?? 20,
  };
}

export function normalizeRecordTasks(r: { items?: Wire<TaskSummary>[]; canAdd?: boolean; manualRules?: Wire<ManualRule>[] }): RecordTasks {
  return {
    items: (r.items ?? []).map(normalizeTaskSummary),
    canAdd: !!r.canAdd,
    manualRules: (r.manualRules ?? []).map((m) => ({
      key: m.key ?? '', name: m.name ?? {}, repeat: m.repeat ?? 'once', applied: !!m.applied, applicable: !!m.applicable,
      previousOpen: !!m.previousOpen,
    })),
  };
}

/** A withdrawn comment arrives without `body`; system entries carry SystemEvent + SystemData instead. */
export function normalizeTaskComment(c: Wire<TaskComment>): TaskComment {
  return {
    id: c.id as number,
    kind: c.kind ?? 'comment',
    authorUserId: c.authorUserId ?? null,
    authorName: c.authorName ?? null,
    body: c.body ?? null,
    withdrawn: !!c.withdrawn,
    systemEvent: c.systemEvent ?? null,
    systemData: c.systemData ?? null,
    createdTime: c.createdTime ?? '',
    mentions: c.mentions ?? [],
    attachments: c.attachments ?? [],
    canWithdraw: !!c.canWithdraw,
  };
}

/** Anything that is not the documented shape counts as an empty list, so a bad reply never breaks the header. */
export function normalizeNotificationList(l: unknown): NotificationList {
  const raw = (l ?? {}) as { items?: unknown; unreadCount?: unknown };
  const items = Array.isArray(raw.items) ? (raw.items as Wire<NotificationItem>[]) : [];
  return {
    items: items.map((n) => ({
      id: n.id as number, kind: n.kind ?? '', title: n.title ?? '', link: n.link ?? '', read: !!n.read, createdTime: n.createdTime ?? '',
    })),
    unreadCount: typeof raw.unreadCount === 'number' ? raw.unreadCount : 0,
  };
}

export function normalizeTaskRule(r: Wire<TaskRule>): TaskRule {
  return {
    key: r.key ?? '', name: r.name ?? {}, moduleKey: r.moduleKey ?? '', active: !!r.active, appKey: r.appKey ?? null,
    repeat: r.repeat ?? 'once', trigger: r.trigger ?? { on: 'manual' }, when: r.when ?? null,
    tasks: (r.tasks ?? []).map((t) => ({ ...t, description: t.description ?? null, dueFieldId: t.dueFieldId ?? null, when: t.when ?? null })),
    warnings: r.warnings ?? [],
  };
}
