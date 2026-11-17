import type { ReportDateRange } from './report-types';
import type { I18nText } from './types';
import type { TaskRecordRef, TaskStatus } from './task-types';

/** 看板分欄依據；伺服器回應沒有帶設定，所以由欄的種類反推（見 groupByOf）。 */
export type BoardGroupBy = 'status' | 'assignee' | 'dueBucket';
export type BoardColumnKind = 'status' | 'dueBucket' | 'user' | 'unassigned';
/** 到期區間欄的欄鍵，順序與伺服器 TaskDueBuckets.Keys 相同。 */
export type DueBucketKey = 'overdue' | 'today' | 'thisWeek' | 'later' | 'none';

/** 版面規則（技術限制，不是業務政策）：每欄最小寬度與欄間距，決定一屏放幾欄（D1）。 */
export const BOARD_MIN_COLUMN_WIDTH = 240;
export const BOARD_COLUMN_GAP = 12;

export interface BoardCard {
  id: number;
  title: string;
  status: TaskStatus;
  assigneeUserId: string | null;
  assigneeName: string | null;
  assigneeActive: boolean;
  dueDate: string | null;
  overdue: boolean;
  dueToday: boolean;
  /** 看不到記錄時只有 accessible=false，其餘是 null（設計 §7）。 */
  record: TaskRecordRef | null;
  fromRule: boolean;
  frozen: boolean;
  commentCount: number;
  completedTime: string | null;
  /** 移動時帶回的併發版本（J4）。 */
  version: string;
  canEdit: boolean;
  canReassign: boolean;
}

export interface BoardColumn {
  /** todo、overdue、unassigned、user:<帳號> 等；也是欄分頁 API 的路徑片段。 */
  key: string;
  kind: BoardColumnKind;
  userId: string | null;
  /** 只有 user 欄有；查不到使用者時是 null。 */
  name: string | null;
  inactive: boolean;
  /** 整欄總數，不受已載入張數限制。 */
  total: number;
  cards: BoardCard[];
  hasMore: boolean;
  /** 總數超過畫面上限；此時最後一頁之後顯示「還有 N 筆」。 */
  truncated: boolean;
  /** 不能放的原因由前端依欄種類與欄鍵推出代碼再翻譯（dropReasonOf），不顯示伺服器文字。 */
  droppable: boolean;
}

export interface TaskBoardData {
  /** 以下三個日期由伺服器算好，前端不自己算邊界（T8）。 */
  today: string;
  weekEnd: string;
  laterDefault: string;
  /** false：範圍是「部屬的任務」但目前沒有部屬或政策關閉（D7）。 */
  scopeAvailable: boolean;
  columns: BoardColumn[];
  /** 超過 50 欄而沒列出的負責人數。 */
  omittedAssignees: number;
}

export interface BoardColumnPage {
  key: string;
  total: number;
  cards: BoardCard[];
  hasMore: boolean;
  truncated: boolean;
}

/** 看板資料來源：存檔的看板 id 與未存的設定二擇一；都沒給是預設看板，由伺服器決定內容。 */
export interface BoardSource {
  viewId?: number | null;
  config?: TaskBoardConfig | null;
}

export interface TaskMoveBody {
  groupBy: BoardGroupBy;
  toColumn: string;
  /** 只有移到「之後」欄時帶。 */
  dueDate?: string;
  expectedVersion: string;
}

/** 移動回應：visible=false 代表移動後操作者看不到這張任務了（不回卡片，改負責人時附復原憑證）。 */
export interface TaskMoveResult {
  visible: boolean;
  card: BoardCard | null;
  undoToken: string | null;
}

/** 看板設定（伺服器 TaskBoardConfig，J6）；欄位照後端，前端只負責傳遞。 */
export interface TaskBoardConfig {
  version: number;
  groupBy: BoardGroupBy;
  scope: 'assigned' | 'involved' | 'subordinates' | 'visible';
  filters?: {
    statuses?: TaskStatus[];
    assigneeUserIds?: string[];
    includeUnassigned?: boolean;
    creatorUserIds?: string[];
    sources?: ('personal' | 'record' | 'rule')[];
    dueRange?: ReportDateRange;
    keyword?: string;
  };
  columns?: { order?: string[]; hidden?: string[]; assignees?: string[]; autoAppendAssignees?: boolean };
  sort?: { key: 'dueDate' | 'createdTime' | 'title'; dir: 'asc' | 'desc' };
}

export interface TaskViewSummary {
  id: number;
  kind: string;
  name: string;
  ownerUserId: string;
  ownerName: string | null;
  ownerActive: boolean;
  isOwner: boolean;
  shareCount: number;
  modifiedTime: string | null;
}

export interface TaskViewList {
  mine: TaskViewSummary[];
  shared: TaskViewSummary[];
}

export type TaskViewShareKind = 'user' | 'group' | 'department';

export interface TaskViewShare {
  kind: TaskViewShareKind;
  id: string;
  displayName: string | null;
  nameI18n: I18nText | null;
  inactive: boolean;
}

export interface TaskViewDetail {
  id: number;
  kind: string;
  name: string;
  ownerUserId: string;
  ownerName: string | null;
  ownerActive: boolean;
  isOwner: boolean;
  canEdit: boolean;
  canDelete: boolean;
  config: TaskBoardConfig;
  shares: TaskViewShare[];
  rowVersion: string;
}

export interface TaskViewShareTargets {
  users: { id: string; name: string }[];
  departments: { id: number; name: I18nText }[];
  groups: { id: number; name: I18nText }[];
}

/** 一屏放幾欄：每欄至少 BOARD_MIN_COLUMN_WIDTH，欄間距算進去；量不到寬度（0）時當一欄。手機（390）自然是 1。 */
export function columnsPerScreen(width: number): number {
  if (!(width > 0)) return 1;
  return Math.max(1, Math.floor((width + BOARD_COLUMN_GAP) / (BOARD_MIN_COLUMN_WIDTH + BOARD_COLUMN_GAP)));
}

export interface VisibleRange {
  /** 夾在合法範圍內的組別（0 起算）。 */
  page: number;
  pageCount: number;
  /** 顯示的欄位索引區間 [start, end)。 */
  start: number;
  end: number;
}

/** 第 page 組要顯示哪幾欄；超出範圍夾回第一組或最後一組，沒有欄時是空區間。 */
export function visibleRange(page: number, perScreen: number, count: number): VisibleRange {
  const size = Math.max(1, Math.floor(perScreen));
  const pageCount = Math.max(1, Math.ceil(Math.max(0, count) / size));
  const clamped = Math.min(Math.max(0, Math.floor(page) || 0), pageCount - 1);
  const start = Math.min(clamped * size, Math.max(0, count));
  return { page: clamped, pageCount, start, end: Math.min(start + size, Math.max(0, count)) };
}

/** 伺服器沒有回分欄依據，由欄的種類反推；沒有任何欄時退回依狀態。 */
export function groupByOf(columns: readonly BoardColumn[]): BoardGroupBy {
  const kind = columns[0]?.kind;
  return kind === 'dueBucket' ? 'dueBucket' : kind === 'user' || kind === 'unassigned' ? 'assignee' : 'status';
}

/** 欄不能放的原因代碼；unavailable 是前端還不認得的欄規則（後端新增時的備援）。 */
export type DropReason = 'overdue' | 'thisWeekEmpty' | 'inactiveAssignee' | 'unavailable';
export type MoveBlock = { kind: 'column'; reason: DropReason } | { kind: 'noPermission' } | { kind: 'frozen' };

/** 不能放的欄：逾期欄、今天已是本週最後一天的本週欄、已停用的負責人欄；其他視為 unavailable。 */
export function dropReasonOf(c: BoardColumn): DropReason {
  if (c.kind === 'dueBucket' && c.key === 'overdue') return 'overdue';
  if (c.kind === 'dueBucket' && c.key === 'thisWeek') return 'thisWeekEmpty';
  if (c.kind === 'user') return 'inactiveAssignee';
  return 'unavailable';
}

/** 原因文字的 i18n 鍵（taskBoard.drop.*）。 */
export const moveBlockKey = (b: MoveBlock) => `taskBoard.drop.${b.kind === 'column' ? b.reason : b.kind}`;

/** 格線欄數：全部欄一屏放得下時用實際欄數（撐滿寬度）；要分段平移時固定一屏欄數，最後一組右邊留空。 */
export const gridTrackCount = (columnCount: number, perScreen: number) => Math.max(1, Math.min(columnCount, perScreen));

export interface MoveTarget {
  key: string;
  /** 卡片現在就在這一欄。 */
  current: boolean;
  droppable: boolean;
  reason: MoveBlock | null;
}

/**
 * 每一欄這張卡能不能放、不能放的原因。依負責人分欄看「可重派」，其他看「可編輯」；凍結的卡整張不能動；
 * 其餘是欄本身不能放（原因見 dropReasonOf；能不能放由伺服器的 droppable 決定）。
 */
export function moveTargets(card: BoardCard, columns: readonly BoardColumn[], groupBy: BoardGroupBy): MoveTarget[] {
  const cardBlock: MoveBlock | null = card.frozen
    ? { kind: 'frozen' }
    : (groupBy === 'assignee' ? card.canReassign : card.canEdit) ? null : { kind: 'noPermission' };
  return columns.map((c) => {
    const current = c.cards.some((x) => x.id === card.id);
    const reason: MoveBlock | null = cardBlock ?? (c.droppable ? null : { kind: 'column', reason: dropReasonOf(c) });
    return { key: c.key, current, droppable: reason === null, reason };
  });
}
