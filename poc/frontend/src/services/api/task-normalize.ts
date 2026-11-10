import type {
  ManualRule, RecordTasks, TaskComment, TaskDetail, TaskPage, TaskRecordRef, TaskSummary,
} from 'src/domain/dynamic/task-types';

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
      key: m.key ?? '', name: m.name ?? {}, applied: !!m.applied, applicable: !!m.applicable,
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
