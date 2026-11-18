import type { BoardCard, GanttParent, GanttSpan, TaskGanttConfig, TaskGanttData, TaskGanttWindow } from './task-board-types';

/**
 * 甘特圖的純函式（看板設計 §11.5.4）：日期運算、時間窗平移、長條位置、刻度、分組。
 * 日期一律是 yyyy-MM-dd 字串，換算成「自 1970-01-01 起的天數」計算，不經過瀏覽器時區；今天與週起始日由伺服器回（T8、政策 A7）。
 */
export type GanttScale = TaskGanttConfig['scale'];
export type GanttGroupBy = TaskGanttConfig['groupBy'];

/** 畫面常數（技術限制，設計 §14、GT-T18）：左欄寬、列高、各刻度一屏的長度。 */
export const GANTT_LEFT_WIDTH = 280;
export const GANTT_ROW_HEIGHT = 36;
export const GANTT_DAY_SPAN = 14;
export const GANTT_WEEK_SPAN = 13;
export const GANTT_MONTH_SPAN = 6;

const DAY_MS = 86_400_000;
const WEEKDAYS = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];

export const dayNumber = (iso: string): number => {
  const [y, m, d] = iso.split('-').map(Number);
  return Math.round(Date.UTC(y, m - 1, d) / DAY_MS);
};
export const isoOfDay = (n: number): string => new Date(n * DAY_MS).toISOString().slice(0, 10);
export const addDays = (iso: string, n: number): string => isoOfDay(dayNumber(iso) + n);
export function addMonths(iso: string, n: number): string {
  const [y, m] = iso.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1 + n, 1)).toISOString().slice(0, 10);
}
export const startOfMonth = (iso: string): string => `${iso.slice(0, 8)}01`;
/** 週起始日（政策值 monday、sunday…）所在那週的第一天；不認得的值當成星期一。 */
export function startOfWeek(iso: string, weekStart: string): string {
  const first = WEEKDAYS.indexOf(weekStart.toLowerCase());
  const want = first < 0 ? 1 : first;
  const dow = new Date(dayNumber(iso) * DAY_MS).getUTCDay();
  return addDays(iso, -((dow - want + 7) % 7));
}
/** 時間窗天數（含頭含尾）。 */
export const windowDays = (w: TaskGanttWindow): number => dayNumber(w.to) - dayNumber(w.from) + 1;

/** 從某天起算一屏：日刻度 14 天、週 13 週（都從週起始日起）、月 6 個月（從當月一日起）。換刻度時用目前時間窗的起點重新起算，不跳回今天。 */
export function windowFor(scale: GanttScale, anchor: string, weekStart: string): TaskGanttWindow {
  if (scale === 'month') {
    const from = startOfMonth(anchor);
    return { from, to: addDays(addMonths(from, GANTT_MONTH_SPAN), -1) };
  }
  const from = startOfWeek(anchor, weekStart);
  return { from, to: addDays(from, (scale === 'day' ? GANTT_DAY_SPAN : GANTT_WEEK_SPAN * 7) - 1) };
}

/** 上一段／下一段：日 ±14 天、週 ±13 週、月 ±6 個月。 */
export function shiftWindow(w: TaskGanttWindow, scale: GanttScale, dir: 1 | -1): TaskGanttWindow {
  if (scale === 'month') {
    const from = addMonths(startOfMonth(w.from), dir * GANTT_MONTH_SPAN);
    return { from, to: addDays(addMonths(from, GANTT_MONTH_SPAN), -1) };
  }
  const step = dir * (scale === 'day' ? GANTT_DAY_SPAN : GANTT_WEEK_SPAN * 7);
  return { from: addDays(w.from, step), to: addDays(w.to, step) };
}

export interface BarGeometry {
  /** bar：有開始日與到期日；milestone：只有到期日（菱形）。 */
  kind: 'bar' | 'milestone';
  /** 佔時間軸寬度的比例（0–1）。 */
  left: number;
  width: number;
  /** 實際的開始日早於時間窗、到期日晚於時間窗：邊緣畫箭頭。 */
  clippedStart: boolean;
  clippedEnd: boolean;
  /** 整條都在時間窗之外（只有時間窗外的父任務會這樣）：只在那一側邊緣畫箭頭。 */
  outside: 'before' | 'after' | null;
}

/** 長條從開始日左緣畫到到期日右緣（含頭含尾）；超出時間窗的部分截掉。沒有到期日不上時間軸（null）。 */
export function barGeometry(startDate: string | null, dueDate: string | null, w: TaskGanttWindow): BarGeometry | null {
  if (!dueDate) return null;
  const from = dayNumber(w.from);
  const to = dayNumber(w.to);
  const days = to - from + 1;
  const s = dayNumber(startDate ?? dueDate);
  const e = dayNumber(dueDate);
  const kind = startDate ? 'bar' : 'milestone';
  if (e < from) return { kind, left: 0, width: 0, clippedStart: true, clippedEnd: false, outside: 'before' };
  if (s > to) return { kind, left: 1, width: 0, clippedStart: false, clippedEnd: true, outside: 'after' };
  const cs = Math.max(s, from);
  const ce = Math.min(e, to);
  return { kind, left: (cs - from) / days, width: (ce - cs + 1) / days, clippedStart: s < from, clippedEnd: e > to, outside: null };
}

/** 子任務跨度的位置；與時間窗沒有交集時不畫。 */
export function spanGeometry(span: GanttSpan | null, w: TaskGanttWindow): { left: number; width: number } | null {
  if (!span) return null;
  const g = barGeometry(span.from, span.to, w);
  return g && g.outside === null ? { left: g.left, width: g.width } : null;
}

/** 今天在時間窗內時，回今天那一格中央的位置比例；不在就不畫（G52）。 */
export function todayPosition(today: string, w: TaskGanttWindow): number | null {
  if (!today || today < w.from || today > w.to) return null;
  return (dayNumber(today) - dayNumber(w.from) + 0.5) / windowDays(w);
}

export interface GanttTick {
  /** 這一格的第一天。 */
  date: string;
  left: number;
  width: number;
}

const segments = (starts: string[], w: TaskGanttWindow): GanttTick[] => {
  const from = dayNumber(w.from);
  const days = windowDays(w);
  return starts.map((date, i) => {
    const s = Math.max(dayNumber(date), from);
    const e = i + 1 < starts.length ? dayNumber(starts[i + 1]) : dayNumber(w.to) + 1;
    return { date, left: (s - from) / days, width: (e - s) / days };
  });
};

/** 時間窗裡每個月的區段（第一段從時間窗起點開始）。 */
export function monthTicks(w: TaskGanttWindow): GanttTick[] {
  const starts: string[] = [];
  for (let m = startOfMonth(w.from); m <= w.to; m = addMonths(m, 1)) starts.push(m);
  return segments(starts, w);
}

/** 下排刻度：日刻度每天一格、週刻度每週一格（從時間窗起點每 7 天）；月刻度沒有下排。 */
export function minorTicks(w: TaskGanttWindow, scale: GanttScale): GanttTick[] {
  if (scale === 'month') return [];
  const step = scale === 'day' ? 1 : 7;
  const starts: string[] = [];
  for (let d = w.from; d <= w.to; d = addDays(d, step)) starts.push(d);
  return segments(starts, w);
}

/** 依語系格式化日期（UTC，避免瀏覽器時區把日期推前一天）。 */
export const formatGanttDate = (iso: string, locale: string, opts: Intl.DateTimeFormatOptions): string =>
  new Intl.DateTimeFormat(locale, { ...opts, timeZone: 'UTC' }).format(new Date(dayNumber(iso) * DAY_MS));

// ---- 分組（GT-T10：伺服器回平鋪的列，分組在前端做） ----

export interface GanttTaskLine {
  kind: 'task';
  key: string;
  card: BoardCard;
  /** 1：依父子分組時縮排的子任務。 */
  depth: 0 | 1;
  /** 這一列是有子列的父任務時才有：子列數（只算時間軸上的列）。 */
  childCount: number;
  collapsed: boolean;
  span: GanttSpan | null;
}
/** 子任務在結果裡、父任務不在：淡色標題，只有標題與跨度、不畫自己的長條、不算進列數（GT-Q2）。 */
export interface GanttParentLine {
  kind: 'parent';
  key: string;
  parent: GanttParent;
  childCount: number;
  collapsed: boolean;
  span: GanttSpan | null;
}
/** 依負責人分組的組標題；userId 是 null 代表未指派。 */
export interface GanttGroupLine {
  kind: 'group';
  key: string;
  userId: string | null;
  name: string | null;
  inactive: boolean;
  count: number;
}
export type GanttLine = GanttTaskLine | GanttParentLine | GanttGroupLine;

const taskLine = (card: BoardCard, depth: 0 | 1, childCount = 0, collapsed = false, span: GanttSpan | null = null): GanttTaskLine =>
  ({ kind: 'task', key: `t${card.id}`, card, depth, childCount, collapsed, span });

/**
 * 依父子：父任務一列、子任務縮排在下（收合時不列）；父任務不在列裡時改用 `parents` 的資料（有 row 就照畫它自己的長條，沒有就是淡色標題）。
 * 組的順序依組內第一列在伺服器排序裡的位置（§11.5.4.5）；找不到父任務資料的子任務當成獨立的列。
 */
function byParent(data: TaskGanttData, collapsed: ReadonlySet<number>): GanttLine[] {
  const rowIds = new Set(data.rows.map((r) => r.id));
  const parents = new Map(data.parents.map((p) => [p.id, p]));
  const spans = new Map(data.spans.map((s) => [s.parentId, s]));
  const groups = new Map<number, { first: number; head: BoardCard | GanttParent | null; children: BoardCard[] }>();
  const group = (id: number, index: number) => {
    const g = groups.get(id) ?? { first: index, head: null, children: [] };
    g.first = Math.min(g.first, index);
    groups.set(id, g);
    return g;
  };
  data.rows.forEach((r, i) => {
    const pid = r.parentTaskId;
    if (pid !== null && (rowIds.has(pid) || parents.has(pid))) {
      const g = group(pid, i);
      g.children.push(r);
      if (!rowIds.has(pid)) g.head = parents.get(pid)!;
    } else {
      group(r.id, i).head = r;
    }
  });
  const lines: GanttLine[] = [];
  for (const [id, g] of [...groups].sort((a, b) => a[1].first - b[1].first)) {
    const isCollapsed = g.children.length > 0 && collapsed.has(id);
    const span = spans.get(id) ?? null;
    const head = g.head!;
    if ('status' in head) lines.push(taskLine(head, 0, g.children.length, isCollapsed, span));
    else if (head.row) lines.push(taskLine(head.row, 0, g.children.length, isCollapsed, span));
    else lines.push({ kind: 'parent', key: `p${id}`, parent: head, childCount: g.children.length, collapsed: isCollapsed, span });
    if (!isCollapsed) for (const c of g.children) lines.push(taskLine(c, 1));
  }
  return lines;
}

/** 依負責人：只列有任務的人，依姓名排序、「未指派」最後；組內照伺服器順序。 */
function byAssignee(rows: BoardCard[], compare: (a: string, b: string) => number): GanttLine[] {
  const groups = new Map<string, { userId: string | null; name: string | null; inactive: boolean; cards: BoardCard[] }>();
  for (const r of rows) {
    const k = r.assigneeUserId ?? '';
    const g = groups.get(k) ?? { userId: r.assigneeUserId, name: r.assigneeName, inactive: !r.assigneeActive, cards: [] };
    g.cards.push(r);
    groups.set(k, g);
  }
  const label = (g: { userId: string | null; name: string | null }) => g.name ?? g.userId ?? '';
  const sorted = [...groups.values()].sort((a, b) => {
    if (a.userId === null || b.userId === null) return a.userId === null ? (b.userId === null ? 0 : 1) : -1;
    return compare(label(a), label(b)) || compare(a.userId, b.userId);
  });
  return sorted.flatMap((g) => [
    { kind: 'group' as const, key: `g${g.userId ?? ''}`, userId: g.userId, name: g.name, inactive: g.userId !== null && g.inactive, count: g.cards.length },
    ...g.cards.map((c) => taskLine(c, 0)),
  ]);
}

/** 把甘特查詢的平鋪列排成畫面上的一列列。 */
export function ganttLines(data: TaskGanttData, groupBy: GanttGroupBy, collapsed: ReadonlySet<number>, compare: (a: string, b: string) => number): GanttLine[] {
  if (groupBy === 'parent') return byParent(data, collapsed);
  if (groupBy === 'assignee') return byAssignee(data.rows, compare);
  return data.rows.map((r) => taskLine(r, 0));
}
