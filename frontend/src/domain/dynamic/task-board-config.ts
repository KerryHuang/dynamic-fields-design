import type { ReportDateRange } from './report-types';
import type { BoardGroupBy, TaskBoardConfig } from './task-board-types';
import type { TaskStatus } from './task-types';

/**
 * 看板設定的編輯模型（設定對話框用）：設定 ⇄ 表單。後端 `TaskBoardConfig` 是唯一的驗證者，這裡只負責
 * 組出它收得下的形狀（未知屬性會被擋）、並維持「欄順序與隱藏的鍵必須屬於目前的分欄依據」。
 */

export type BoardScope = TaskBoardConfig['scope'];
export type BoardSortKey = NonNullable<TaskBoardConfig['sort']>['key'];
export type TaskSourceKind = 'personal' | 'record' | 'rule';

/** 各分欄依據的固定欄鍵與預設順序（與後端 TaskBoardConfig.FixedColumnsOf 相同）；負責人看板只有「未指派」。 */
export const BOARD_FIXED_COLUMNS: Record<BoardGroupBy, string[]> = {
  status: ['todo', 'inProgress', 'done', 'cancelled'],
  dueBucket: ['overdue', 'today', 'thisWeek', 'later', 'none'],
  assignee: ['unassigned'],
};
export const BOARD_STATUSES: TaskStatus[] = ['todo', 'inProgress', 'done', 'cancelled'];
export const BOARD_SOURCES: TaskSourceKind[] = ['personal', 'record', 'rule'];
export const BOARD_SCOPES: BoardScope[] = ['assigned', 'involved', 'subordinates', 'visible'];
export const BOARD_SORT_KEYS: BoardSortKey[] = ['dueDate', 'createdTime', 'title'];
/** 到期日區間的種類（沿用報表的相對區間）；`lastDays`、`lastWeeks`、`lastMonths` 需要 N。 */
export const BOARD_DUE_RANGES = ['today', 'thisWeek', 'lastWeek', 'thisMonth', 'lastMonth', 'thisYear', 'lastYear', 'lastDays', 'lastWeeks', 'lastMonths'];
export const BOARD_RANGES_WITH_N = ['lastDays', 'lastWeeks', 'lastMonths'];
/** 技術限制（與後端相同）：明列的負責人、關鍵字長度、看板名稱長度、區間 N 的上限（ReportDateRange.MaxN）。 */
export const MAX_EXPLICIT_ASSIGNEES = 50;
export const MAX_KEYWORD = 100;
export const MAX_BOARD_NAME = 100;
export const MAX_RANGE_N = 3650;
/** 「近 N 天」等區間第一次選到時的 N。 */
export const DEFAULT_RANGE_N = 30;

export interface BoardForm {
  groupBy: BoardGroupBy;
  scope: BoardScope;
  statuses: TaskStatus[];
  assigneeUserIds: string[];
  /** 畫面上看到的勾選狀態；沒設定時的預設取決於有沒有列負責人（見 includeUnassignedDefault）。 */
  includeUnassigned: boolean;
  creatorUserIds: string[];
  sources: TaskSourceKind[];
  dueRange: ReportDateRange | null;
  keyword: string;
  /** 固定欄的完整順序（含隱藏的）。 */
  order: string[];
  hidden: string[];
  assignees: string[];
  autoAppend: boolean;
  sortKey: BoardSortKey;
  sortDir: 'asc' | 'desc';
  /** 子任務自己是一張卡（預設開）；關閉時只列父任務與獨立任務。 */
  showSubtasks: boolean;
}

/** 沒明確設定時「含未指派」的預設：沒列負責人就是全部（含未指派），列了負責人就只看那些人。 */
export const includeUnassignedDefault = (assigneeUserIds: readonly string[]) => assigneeUserIds.length === 0;

/**
 * 這位使用者能選的範圍：沒有部屬時不提供「部屬的任務」（新選的時候）；原本就是這個範圍的設定要原樣保留，所以 `current` 是它時仍列出（D7）。
 */
export const scopeOptions = (hasSubordinates: boolean, current?: BoardScope): BoardScope[] =>
  BOARD_SCOPES.filter((s) => hasSubordinates || s !== 'subordinates' || current === 'subordinates');

/** 設定 → 表單；範圍照抄，不替使用者換掉（D7）。 */
export function formFromConfig(config: TaskBoardConfig): BoardForm {
  const f = config.filters ?? {};
  const c = config.columns ?? {};
  const fixed = BOARD_FIXED_COLUMNS[config.groupBy];
  const listed = (c.order ?? []).filter((k) => fixed.includes(k));
  const order = [...new Set([...listed, ...fixed])];
  const assigneeUserIds = [...(f.assigneeUserIds ?? [])];
  return {
    groupBy: config.groupBy,
    scope: config.scope,
    statuses: [...(f.statuses ?? [])],
    assigneeUserIds,
    includeUnassigned: f.includeUnassigned ?? includeUnassignedDefault(assigneeUserIds),
    creatorUserIds: [...(f.creatorUserIds ?? [])],
    sources: [...(f.sources ?? [])],
    dueRange: f.dueRange ? { ...f.dueRange } : null,
    keyword: f.keyword ?? '',
    order,
    hidden: (c.hidden ?? []).filter((k) => fixed.includes(k)),
    assignees: [...(c.assignees ?? [])],
    autoAppend: c.autoAppendAssignees ?? true,
    sortKey: config.sort?.key ?? 'dueDate',
    sortDir: config.sort?.dir ?? 'asc',
    showSubtasks: config.showSubtasks ?? true,
  };
}

/** 改篩選的負責人：「含未指派」若還是舊預設，跟著新預設走（第一次列人時自動變成只看那些人）；使用者動過的不覆蓋。 */
export function setFilterAssignees(form: BoardForm, ids: readonly string[]): void {
  const before = includeUnassignedDefault(form.assigneeUserIds);
  const after = includeUnassignedDefault(ids);
  if (before !== after && form.includeUnassigned === before) form.includeUnassigned = after;
  form.assigneeUserIds = [...ids];
}

/** 換分欄依據：固定欄的鍵不同，順序與隱藏重來；其他設定保留。 */
export function changeGroupBy(form: BoardForm, groupBy: BoardGroupBy): void {
  if (form.groupBy === groupBy) return;
  form.groupBy = groupBy;
  form.order = [...BOARD_FIXED_COLUMNS[groupBy]];
  form.hidden = [];
}

/** 表單 → 設定：沒設定的篩選與欄選項整段省略（後端把空的視同沒設）。順序只有和預設不同才送。 */
export function buildBoardConfig(form: BoardForm): TaskBoardConfig {
  const filters: NonNullable<TaskBoardConfig['filters']> = {};
  if (form.statuses.length) filters.statuses = [...form.statuses];
  if (form.assigneeUserIds.length) filters.assigneeUserIds = [...form.assigneeUserIds];
  if (form.includeUnassigned !== includeUnassignedDefault(form.assigneeUserIds)) filters.includeUnassigned = form.includeUnassigned;
  if (form.creatorUserIds.length) filters.creatorUserIds = [...form.creatorUserIds];
  if (form.sources.length) filters.sources = [...form.sources];
  if (form.dueRange) filters.dueRange = BOARD_RANGES_WITH_N.includes(form.dueRange.kind) ? { kind: form.dueRange.kind, n: form.dueRange.n ?? DEFAULT_RANGE_N } : { kind: form.dueRange.kind };
  const keyword = form.keyword.trim();
  if (keyword) filters.keyword = keyword;

  const columns: NonNullable<TaskBoardConfig['columns']> = {};
  const fixed = BOARD_FIXED_COLUMNS[form.groupBy];
  if (form.groupBy !== 'assignee' && form.order.some((k, i) => k !== fixed[i])) columns.order = [...form.order];
  if (form.hidden.length) columns.hidden = [...form.hidden];
  if (form.groupBy === 'assignee') {
    if (form.assignees.length) columns.assignees = [...form.assignees];
    if (!form.autoAppend) columns.autoAppendAssignees = false;
  }
  return {
    version: 1,
    groupBy: form.groupBy,
    scope: form.scope,
    ...(Object.keys(filters).length ? { filters } : {}),
    ...(Object.keys(columns).length ? { columns } : {}),
    sort: { key: form.sortKey, dir: form.sortDir },
    // 沒給等於開；只有關閉才寫進設定，舊設定與預設看板的形狀不變（不升 version，設計 §6.1）
    ...(form.showSubtasks ? {} : { showSubtasks: false }),
  };
}

/** 「近 N 天」等需要 N 的區間，N 不是 1 到上限的整數（後端會回 400）。 */
export function rangeNInvalid(form: BoardForm): boolean {
  const r = form.dueRange;
  if (!r || !BOARD_RANGES_WITH_N.includes(r.kind)) return false;
  return !Number.isInteger(r.n) || (r.n as number) < 1 || (r.n as number) > MAX_RANGE_N;
}

/** 把清單裡的一項往前（-1）或往後（1）移一格；已在頭尾就原樣回傳。 */
export function moveInList<T>(list: readonly T[], item: T, delta: -1 | 1): T[] {
  const i = list.indexOf(item);
  const j = i + delta;
  const next = [...list];
  if (i < 0 || j < 0 || j >= next.length) return next;
  [next[i], next[j]] = [next[j]!, next[i]!];
  return next;
}

/** 另存為新看板時的設定：沒有部屬的人不能存「部屬的任務」範圍，改成「指派給我」（D7）；其餘照抄。 */
export function saveAsConfig(config: TaskBoardConfig, hasSubordinates: boolean): TaskBoardConfig {
  return !hasSubordinates && config.scope === 'subordinates' ? { ...config, scope: 'assigned' } : config;
}
