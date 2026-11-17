import type { ReportDateRange } from './report-types';
import type { I18nText } from './types';
import type { TaskRecordRef, TaskStatus } from './task-types';

/** 看板分欄依據；欄查詢的回應帶著採用的設定（TaskBoardData.config）。 */
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
  /** 這份回應實際採用的看板設定（存檔的、未存的、預設的都帶）：分欄依據與預設看板的起點以它為準，前端不另存預設值。 */
  config: TaskBoardConfig;
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

/** 拖拉中一欄的樣子：能不能放、不能放的原因（已翻譯成介面語言）；指標在不在上面是另外的 over，才不會每次移動都重算全部欄。 */
export interface ColumnDrop { allowed: boolean; reason: string | null }

export interface MoveTarget {
  key: string;
  /** 卡片現在就在這一欄。 */
  current: boolean;
  droppable: boolean;
  reason: MoveBlock | null;
}

/** 這張卡整張能不能動；null＝可以。凍結不能動；依負責人分欄看「可編輯或可重派」，其他看「可編輯」（與後端 MoveTaskHandler 相同）。 */
export function cardMoveBlock(card: BoardCard, groupBy: BoardGroupBy): MoveBlock | null {
  if (card.frozen) return { kind: 'frozen' };
  return (groupBy === 'assignee' ? card.canEdit || card.canReassign : card.canEdit) ? null : { kind: 'noPermission' };
}

/**
 * 每一欄這張卡能不能放、不能放的原因。卡片整張不能動時每欄都帶同一個原因；
 * 其餘是欄本身不能放（原因見 dropReasonOf；能不能放由伺服器的 droppable 決定）。
 */
export function moveTargets(card: BoardCard, columns: readonly BoardColumn[], groupBy: BoardGroupBy): MoveTarget[] {
  const cardBlock = cardMoveBlock(card, groupBy);
  return columns.map((c) => {
    const current = c.cards.some((x) => x.id === card.id);
    const reason: MoveBlock | null = cardBlock ?? (c.droppable ? null : { kind: 'column', reason: dropReasonOf(c) });
    return { key: c.key, current, droppable: reason === null, reason };
  });
}

/** 放進這一欄前要走的流程：直接送出、先確認取消（U5）、先確認清空規則任務的到期日（U5）、先選日期（「之後」）。 */
export type MoveFlow = 'direct' | 'confirmCancel' | 'confirmClearRuleDue' | 'pickDate';
export function moveFlowOf(card: BoardCard, to: BoardColumn, groupBy: BoardGroupBy): MoveFlow {
  if (groupBy === 'status' && to.key === 'cancelled') return 'confirmCancel';
  if (groupBy === 'dueBucket' && to.key === 'none' && card.fromRule) return 'confirmClearRuleDue';
  if (groupBy === 'dueBucket' && to.key === 'later') return 'pickDate';
  return 'direct';
}

/** 「之後」只能選本週最後一天之後的日期（yyyy-MM-dd 字串可直接比大小）。 */
export const isLaterDateAllowed = (date: string | null | undefined, weekEnd: string) => !!date && date > weekEnd;

/** 樂觀更新用：把卡片搬到目標欄最上面並改相關欄位，兩欄總數跟著動；找不到卡片或欄時回 null（不動快取）。不改傳入的物件。 */
export function moveCardLocally(
  board: TaskBoardData, cardId: number, toKey: string, groupBy: BoardGroupBy, dueDate: string | undefined,
): TaskBoardData | null {
  const from = board.columns.find((c) => c.cards.some((x) => x.id === cardId));
  const to = board.columns.find((c) => c.key === toKey);
  const card = from?.cards.find((x) => x.id === cardId);
  if (!from || !to || !card) return null;
  const moved: BoardCard = { ...card };
  if (groupBy === 'status') moved.status = toKey as TaskStatus;
  else if (groupBy === 'assignee') {
    moved.assigneeUserId = to.userId;
    moved.assigneeName = to.name;
    moved.assigneeActive = !to.inactive;
  } else {
    const date = toKey === 'today' ? board.today : toKey === 'thisWeek' ? board.weekEnd : toKey === 'later' ? (dueDate ?? null) : null;
    moved.dueDate = date;
    moved.overdue = false;
    moved.dueToday = toKey === 'today';
  }
  if (from.key === to.key) return board;
  return {
    ...board,
    columns: board.columns.map((c) => {
      if (c.key === from.key) return { ...c, total: Math.max(0, c.total - 1), cards: c.cards.filter((x) => x.id !== cardId) };
      if (c.key === to.key) return { ...c, total: c.total + 1, cards: [moved, ...c.cards] };
      return c;
    }),
  };
}

/**
 * 樂觀更新失敗時只放回這一張卡：把原本的卡片物件放回原欄原位置，兩欄總數跟著動，其他欄與其他卡（可能有別的移動還在進行）不碰。
 * 卡片已經回到原欄、已不在看板上（被移出範圍或換頁重抓了）或原欄不見時不動。
 */
export function restoreCardLocally(board: TaskBoardData, original: BoardCard, fromKey: string, index: number): TaskBoardData {
  const now = board.columns.find((c) => c.cards.some((x) => x.id === original.id));
  const home = board.columns.find((c) => c.key === fromKey);
  if (!now || !home || now.key === home.key) return board;
  return {
    ...board,
    columns: board.columns.map((c) => {
      if (c.key === now.key) return { ...c, total: Math.max(0, c.total - 1), cards: c.cards.filter((x) => x.id !== original.id) };
      if (c.key === home.key) {
        const cards = [...c.cards];
        cards.splice(Math.min(index, cards.length), 0, original);
        return { ...c, total: c.total + 1, cards };
      }
      return c;
    }),
  };
}

/** 移動後看不到這張任務了：從畫面上拿掉。 */
export function removeCardLocally(board: TaskBoardData, cardId: number): TaskBoardData {
  return {
    ...board,
    columns: board.columns.map((c) => (c.cards.some((x) => x.id === cardId)
      ? { ...c, total: Math.max(0, c.total - 1), cards: c.cards.filter((x) => x.id !== cardId) }
      : c)),
  };
}
