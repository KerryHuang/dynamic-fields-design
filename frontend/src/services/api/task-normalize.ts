import type {
  ManualRule, NotificationItem, NotificationList, RecordTasks, TaskCapabilities, TaskComment, TaskDetail, TaskLevel, TaskPage, TaskProgress,
  TaskRecordRef, TaskSummary, TaskUpdateResult,
} from 'src/domain/dynamic/task-types';
import type { TaskRule } from 'src/domain/dynamic/task-rule-draft';
import type {
  BoardCard, BoardColumn, BoardColumnPage, GanttParent, GanttSpan, TaskBoardConfig, TaskBoardData, TaskGanttConfig, TaskGanttData, TaskMoveResult,
  TaskScheduleResult, TaskViewConfig, TaskViewDetail, TaskViewList, TaskViewShare, TaskViewShareTargets, TaskViewSummary,
} from 'src/domain/dynamic/task-board-types';

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
    startDate: t.startDate ?? null,
    overdue: !!t.overdue,
    record: normalizeRecord(t.record),
    fromRule: !!t.fromRule,
    canEdit: !!t.canEdit,
    canReassign: !!t.canReassign,
    supervised: !!t.supervised,
    version: t.version ?? '',
    ...normalizeHierarchy(t),
  };
}

const normalizeProgress = (p: Wire<TaskProgress> | null | undefined): TaskProgress | null =>
  (p ? { done: p.done ?? 0, cancelled: p.cancelled ?? 0, total: p.total ?? 0 } : null);

/** Replies from before subtasks existed have none of these: treat them as standalone tasks. */
const normalizeHierarchy = (t: { parentTaskId?: number | null; parentTitle?: string | null; level?: TaskLevel | null; progress?: Wire<TaskProgress> | null }) => ({
  parentTaskId: t.parentTaskId ?? null,
  parentTitle: t.parentTitle ?? null,
  level: t.level ?? 'standalone',
  progress: normalizeProgress(t.progress),
});

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
    parentVisible: !!t.parentVisible,
    subtasks: ((t.subtasks ?? []) as Wire<TaskSummary>[]).map(normalizeTaskSummary),
    canAddSubtask: !!t.canAddSubtask,
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

export function normalizeBoardCard(c: Wire<BoardCard>): BoardCard {
  return {
    id: c.id as number,
    title: c.title ?? '',
    status: c.status ?? 'todo',
    assigneeUserId: c.assigneeUserId ?? null,
    assigneeName: c.assigneeName ?? null,
    assigneeActive: !!c.assigneeActive,
    dueDate: c.dueDate ?? null,
    startDate: c.startDate ?? null,
    overdue: !!c.overdue,
    dueToday: !!c.dueToday,
    record: normalizeRecord(c.record),
    fromRule: !!c.fromRule,
    frozen: !!c.frozen,
    commentCount: c.commentCount ?? 0,
    completedTime: c.completedTime ?? null,
    version: c.version ?? '',
    canEdit: !!c.canEdit,
    canReassign: !!c.canReassign,
    ...normalizeHierarchy(c),
  };
}

export function normalizeBoardColumn(c: Wire<Omit<BoardColumn, 'cards'>> & { cards?: Wire<BoardCard>[] | null }): BoardColumn {
  return {
    key: c.key ?? '',
    kind: c.kind ?? 'status',
    userId: c.userId ?? null,
    name: c.name ?? null,
    inactive: !!c.inactive,
    total: c.total ?? 0,
    cards: (c.cards ?? []).map(normalizeBoardCard),
    hasMore: !!c.hasMore,
    truncated: !!c.truncated,
    droppable: c.droppable !== false,
  };
}

export function normalizeBoard(b: Wire<Omit<TaskBoardData, 'columns' | 'config'>> & { columns?: Parameters<typeof normalizeBoardColumn>[0][] | null; config?: TaskBoardConfig }): TaskBoardData {
  return {
    today: b.today ?? '',
    weekEnd: b.weekEnd ?? '',
    laterDefault: b.laterDefault ?? '',
    scopeAvailable: b.scopeAvailable !== false,
    columns: (b.columns ?? []).map(normalizeBoardColumn),
    omittedAssignees: b.omittedAssignees ?? 0,
    // 伺服器一定會帶（存檔、未存、預設三種都一樣），這裡不補預設值
    config: b.config as TaskBoardConfig,
  };
}

export function normalizeBoardPage(p: Wire<Omit<BoardColumnPage, 'cards'>> & { cards?: Wire<BoardCard>[] | null }): BoardColumnPage {
  return {
    key: p.key ?? '',
    total: p.total ?? 0,
    cards: (p.cards ?? []).map(normalizeBoardCard),
    hasMore: !!p.hasMore,
    truncated: !!p.truncated,
  };
}

export function normalizeMoveResult(r: { visible?: boolean; card?: Wire<BoardCard> | null; undoToken?: string | null }): TaskMoveResult {
  return { visible: r.visible !== false, card: r.card ? normalizeBoardCard(r.card) : null, undoToken: r.undoToken ?? null };
}

const normalizeViewSummary = (v: Wire<TaskViewSummary>): TaskViewSummary => ({
  id: v.id as number, kind: v.kind ?? 'board', name: v.name ?? '', ownerUserId: v.ownerUserId ?? '', ownerName: v.ownerName ?? null,
  ownerActive: !!v.ownerActive, isOwner: !!v.isOwner, shareCount: v.shareCount ?? 0, modifiedTime: v.modifiedTime ?? null,
});

export const normalizeViewList = (l: { mine?: Wire<TaskViewSummary>[] | null; shared?: Wire<TaskViewSummary>[] | null }): TaskViewList => ({
  mine: (l.mine ?? []).map(normalizeViewSummary),
  shared: (l.shared ?? []).map(normalizeViewSummary),
});

const normalizeShare = (s: Wire<TaskViewShare>): TaskViewShare => ({
  kind: s.kind ?? 'user', id: s.id ?? '', displayName: s.displayName ?? null, nameI18n: s.nameI18n ?? null, inactive: !!s.inactive,
});

type WireGantt = Wire<Omit<TaskGanttData, 'rows' | 'unscheduled' | 'parents' | 'spans' | 'window' | 'config'>> & {
  rows?: Wire<BoardCard>[] | null;
  unscheduled?: Wire<BoardCard>[] | null;
  parents?: (Wire<Omit<GanttParent, 'row'>> & { row?: Wire<BoardCard> | null })[] | null;
  spans?: Wire<GanttSpan>[] | null;
  window?: { from?: string | null; to?: string | null } | null;
  config?: TaskGanttConfig;
};

export function normalizeGantt(g: WireGantt): TaskGanttData {
  return {
    today: g.today ?? '',
    weekStart: g.weekStart ?? 'monday',
    window: { from: g.window?.from ?? '', to: g.window?.to ?? '' },
    scopeAvailable: g.scopeAvailable !== false,
    // 伺服器一定會帶實際採用的設定（存檔、未存、預設三種都一樣），這裡不補預設值
    config: g.config as TaskGanttConfig,
    rows: (g.rows ?? []).map(normalizeBoardCard),
    total: g.total ?? 0,
    truncated: !!g.truncated,
    parents: (g.parents ?? []).map((p) => ({
      id: p.id as number, title: p.title ?? '', visible: !!p.visible, row: p.row ? normalizeBoardCard(p.row) : null,
    })),
    spans: (g.spans ?? []).map((s) => ({ parentId: s.parentId as number, from: s.from ?? '', to: s.to ?? '' })),
    unscheduled: (g.unscheduled ?? []).map(normalizeBoardCard),
    unscheduledTotal: g.unscheduledTotal ?? 0,
    unscheduledTruncated: !!g.unscheduledTruncated,
  };
}

export function normalizeScheduleResult(r: { card?: Wire<BoardCard> | null }): TaskScheduleResult {
  return { card: normalizeBoardCard(r.card ?? {}) };
}

export function normalizeViewDetail<C extends TaskViewConfig = TaskBoardConfig>(
  v: Wire<Omit<TaskViewDetail<C>, 'shares' | 'config'>> & { config?: C; shares?: Wire<TaskViewShare>[] | null },
): TaskViewDetail<C> {
  return {
    id: v.id as number, kind: v.kind ?? 'board', name: v.name ?? '', ownerUserId: v.ownerUserId ?? '', ownerName: v.ownerName ?? null,
    ownerActive: !!v.ownerActive, isOwner: !!v.isOwner, canEdit: !!v.canEdit, canDelete: !!v.canDelete,
    config: v.config as C, shares: (v.shares ?? []).map(normalizeShare), rowVersion: v.rowVersion ?? '',
  };
}

export const normalizeShareTargets = (t: Partial<TaskViewShareTargets>): TaskViewShareTargets => ({
  users: t.users ?? [], departments: t.departments ?? [], groups: t.groups ?? [],
});
