import { describe, expect, it } from 'vitest';
import { normalizeGantt } from 'src/services/api/task-normalize';
import { cardWire } from 'src/presentation/tasks/board/board-fixtures';
import { ganttWire } from 'src/presentation/tasks/gantt/gantt-fixtures';
import {
  addMonths, barGeometry, canSchedule, formatGanttDate, ganttLines, ganttWeeks, minorTicks, monthTicks, phoneWindow, replaceCardLocally, scheduleCardLocally,
  shiftPhoneWindow, shiftWindow, spanGeometry, startOfWeek, todayPosition, windowDays, windowFor,
} from './task-gantt';

const data = (o: Record<string, unknown> = {}) => normalizeGantt(ganttWire(o) as never);
const cmp = new Intl.Collator('zh-TW').compare;
const keys = (o: Record<string, unknown> = {}, groupBy: 'none' | 'parent' | 'assignee' = 'parent', collapsed: number[] = []) =>
  ganttLines(data(o), groupBy, new Set(collapsed), cmp).map((l) => l.key);

describe('甘特圖日期運算', () => {
  it('週起始日依政策值；不認得的值當星期一', () => {
    expect(startOfWeek('2026-11-18', 'monday')).toBe('2026-11-16');
    expect(startOfWeek('2026-11-18', 'sunday')).toBe('2026-11-15');
    expect(startOfWeek('2026-11-16', 'monday')).toBe('2026-11-16');
    expect(startOfWeek('2026-11-18', '???')).toBe('2026-11-16');
    expect(addMonths('2026-11-30', 3)).toBe('2027-02-01');
  });

  it('0001–0099 年照實際年份算，不被當成 19xx 年（DateOnly 從 0001 起都合法）', () => {
    expect(windowDays({ from: '0099-12-31', to: '0100-01-01' })).toBe(2);
    expect(addMonths('0099-11-15', 2)).toBe('0100-01-01');
    expect(windowDays({ from: '0001-01-01', to: '0001-12-31' })).toBe(365);
  });

  it('G51 一屏：日 14 天、週 13 週、月 6 個月', () => {
    expect(windowFor('day', '2026-11-18', 'monday')).toEqual({ from: '2026-11-16', to: '2026-11-29' });
    expect(windowFor('week', '2026-11-18', 'monday')).toEqual({ from: '2026-11-16', to: '2027-02-14' });
    expect(windowDays(windowFor('week', '2026-11-18', 'monday'))).toBe(91);
    expect(windowFor('month', '2026-11-18', 'monday')).toEqual({ from: '2026-11-01', to: '2027-04-30' });
  });

  it('G51 上一段／下一段平移一屏', () => {
    expect(shiftWindow({ from: '2026-11-16', to: '2026-11-29' }, 'day', 1)).toEqual({ from: '2026-11-30', to: '2026-12-13' });
    expect(shiftWindow({ from: '2026-11-16', to: '2027-02-14' }, 'week', 1)).toEqual({ from: '2027-02-15', to: '2027-05-16' });
    expect(shiftWindow({ from: '2026-11-16', to: '2027-02-14' }, 'week', -1)).toEqual({ from: '2026-08-17', to: '2026-11-15' });
    expect(shiftWindow({ from: '2026-11-01', to: '2027-04-30' }, 'month', 1)).toEqual({ from: '2027-05-01', to: '2027-10-31' });
  });

  it('G53 長條含頭含尾；只有到期日是菱形；跨出時間窗的一側截掉並標出', () => {
    const w = { from: '2026-11-16', to: '2026-11-29' };
    expect(barGeometry('2026-11-16', '2026-11-29', w)).toMatchObject({ kind: 'bar', left: 0, width: 1, clippedStart: false, clippedEnd: false, outside: null });
    expect(barGeometry(null, '2026-11-18', w)).toMatchObject({ kind: 'milestone', left: 2 / 14, width: 1 / 14 });
    expect(barGeometry('2026-11-10', '2026-12-10', w)).toMatchObject({ left: 0, width: 1, clippedStart: true, clippedEnd: true });
    expect(barGeometry('2026-11-01', '2026-11-05', w)).toMatchObject({ outside: 'before' });
    expect(barGeometry('2026-12-01', '2026-12-05', w)).toMatchObject({ outside: 'after' });
    expect(barGeometry('2026-11-20', null, w)).toBeNull();
    expect(spanGeometry({ parentId: 1, from: '2026-11-01', to: '2026-11-05' }, w)).toBeNull();
  });

  it('G52 今天在時間窗內才有位置', () => {
    expect(todayPosition('2026-11-18', { from: '2026-11-16', to: '2026-11-29' })).toBeCloseTo(2.5 / 14);
    expect(todayPosition('2026-11-18', { from: '2026-11-30', to: '2026-12-13' })).toBeNull();
  });

  it('刻度：月份區段從時間窗起點算；週刻度每 7 天一格，月刻度沒有下排', () => {
    const w = { from: '2026-11-16', to: '2027-02-14' };
    expect(monthTicks(w).map((x) => x.date)).toEqual(['2026-11-01', '2026-12-01', '2027-01-01', '2027-02-01']);
    expect(monthTicks(w)[0]).toMatchObject({ left: 0, width: 15 / 91 });
    expect(minorTicks(w, 'week')).toHaveLength(13);
    expect(minorTicks({ from: '2026-11-16', to: '2026-11-29' }, 'day')).toHaveLength(14);
    expect(minorTicks(w, 'month')).toEqual([]);
  });

  it('G61 月份與星期依語系格式化', () => {
    expect(formatGanttDate('2026-11-16', 'zh-TW', { weekday: 'short' })).toBe('週一');
    expect(formatGanttDate('2026-11-16', 'en-US', { weekday: 'short' })).toBe('Mon');
    expect(formatGanttDate('2026-11-16', 'en-US', { month: 'short' })).toBe('Nov');
    expect(formatGanttDate('2026-11-16', 'vi', { month: 'numeric', day: 'numeric' })).toBe('16/11');
    expect(formatGanttDate('2026-11-16', 'zh-TW', { month: 'numeric', day: 'numeric' })).toBe('11/16');
  });
});

describe('甘特圖分組', () => {
  it('G54 依父子：子任務在父任務下；收合後不列', () => {
    expect(keys()).toEqual(['t5', 't6', 't7', 't8', 't9']);
    const lines = ganttLines(data(), 'parent', new Set(), cmp);
    expect(lines[0]).toMatchObject({ kind: 'task', childCount: 2, span: { parentId: 5 } });
    expect(lines[1]).toMatchObject({ kind: 'task', depth: 1 });
    expect(keys({}, 'parent', [5])).toEqual(['t5', 't8', 't9']);
  });

  it('G54 父任務不在列裡：用 parents 的淡色標題（或它自己的列），組的順序依組內第一列', () => {
    const rows = [
      cardWire({ id: 8, title: '寄合約', dueDate: '2026-11-20' }),
      cardWire({ id: 6, title: '子一', startDate: '2026-11-21', dueDate: '2026-11-25', level: 'child', parentTaskId: 5 }),
      cardWire({ id: 11, title: '子二', dueDate: '2026-11-22', level: 'child', parentTaskId: 10 }),
      cardWire({ id: 12, title: '孤兒', dueDate: '2026-11-23', level: 'child', parentTaskId: 99 }),
    ];
    const parents = [
      { id: 5, title: '規劃活動內容', visible: true },
      { id: 10, title: '別人的父任務', visible: true, row: cardWire({ id: 10, title: '別人的父任務', dueDate: '2026-10-01' }) },
    ];
    const lines = ganttLines(data({ rows, parents }), 'parent', new Set(), cmp);
    expect(lines.map((l) => l.key)).toEqual(['t8', 'p5', 't6', 't10', 't11', 't12']);
    expect(lines[1]).toMatchObject({ kind: 'parent', childCount: 1 });
    expect(lines[3]).toMatchObject({ kind: 'task', childCount: 1 });
  });

  it('G55 依負責人：依姓名排序、停用者標記、未指派最後、組內照伺服器順序', () => {
    const rows = [
      cardWire({ id: 1, title: 'a', assigneeUserId: null, assigneeName: null }),
      cardWire({ id: 2, title: 'b', assigneeUserId: 'u3', assigneeName: 'Carl', assigneeActive: false }),
      cardWire({ id: 3, title: 'c', assigneeUserId: 'u2', assigneeName: 'Bob' }),
      cardWire({ id: 4, title: 'd', assigneeUserId: 'u1', assigneeName: 'Amy' }),
      cardWire({ id: 5, title: 'e', assigneeUserId: 'u3', assigneeName: 'Carl', assigneeActive: false }),
    ];
    const lines = ganttLines(data({ rows }), 'assignee', new Set(), new Intl.Collator('en-US').compare);
    expect(lines.map((l) => l.key)).toEqual(['gu1', 't4', 'gu2', 't3', 'gu3', 't2', 't5', 'g', 't1']);
    expect(lines.filter((l) => l.kind === 'group').map((l) => (l.kind === 'group' ? [l.userId, l.inactive, l.count] : []))).toEqual([
      ['u1', false, 1], ['u2', false, 1], ['u3', true, 2], [null, false, 1],
    ]);
  });

  it('不分組是平鋪', () => {
    expect(keys({}, 'none')).toEqual(['t5', 't6', 't7', 't8', 't9']);
  });
});

describe('甘特圖手機週清單與改日期（gt2b1）', () => {
  it('手機時間窗從本週起 4 週，上一段／下一段 ±4 週', () => {
    expect(phoneWindow('2026-11-18', 'monday')).toEqual({ from: '2026-11-16', to: '2026-12-13' });
    expect(phoneWindow('2026-11-18', 'sunday')).toEqual({ from: '2026-11-15', to: '2026-12-12' });
    expect(shiftPhoneWindow({ from: '2026-11-16', to: '2026-12-13' }, 1)).toEqual({ from: '2026-12-14', to: '2027-01-10' });
    expect(shiftPhoneWindow({ from: '2026-11-16', to: '2026-12-13' }, -1)).toEqual({ from: '2026-10-19', to: '2026-11-15' });
  });

  it('GT-Q3 跨週任務只放開始那週；開始日早於時間窗放第一週；只有到期日用到期日；空的週也列', () => {
    const rows = data({
      rows: [
        cardWire({ id: 1, startDate: '2026-11-23', dueDate: '2026-12-09' }),
        cardWire({ id: 2, startDate: '2026-11-01', dueDate: '2026-11-17' }),
        cardWire({ id: 3, dueDate: '2026-11-30' }),
      ],
    }).rows;
    const weeks = ganttWeeks(rows, { from: '2026-11-16', to: '2026-12-13' });
    expect(weeks.map((w) => [w.from, w.to, w.cards.map((c) => c.id)])).toEqual([
      ['2026-11-16', '2026-11-22', [2]],
      ['2026-11-23', '2026-11-29', [1]],
      ['2026-11-30', '2026-12-06', [3]],
      ['2026-12-07', '2026-12-13', []],
    ]);
  });

  it('能不能改日期照卡片的權限欄位：凍結或不能編輯都不行', () => {
    const [a] = data({ rows: [cardWire({ id: 1 })] }).rows;
    expect(canSchedule(a)).toBe(true);
    expect(canSchedule({ ...a, canEdit: false })).toBe(false);
    expect(canSchedule({ ...a, frozen: true })).toBe(false);
  });

  it('先在畫面上改日期：列裡就地改；清掉到期日移到未排程；未排程有了到期日移到時間軸', () => {
    const d = data();
    const moved = scheduleCardLocally(d, 7, '2026-11-30', '2026-12-10');
    expect(moved.rows.find((c) => c.id === 7)).toMatchObject({ startDate: '2026-11-30', dueDate: '2026-12-10' });
    expect(moved.rows.map((c) => c.id)).toEqual(d.rows.map((c) => c.id));
    const cleared = scheduleCardLocally(d, 8, null, null);
    expect(cleared.rows.some((c) => c.id === 8)).toBe(false);
    expect(cleared.unscheduled[0]).toMatchObject({ id: 8, dueDate: null });
    expect(cleared.unscheduledTotal).toBe(3);
    const placed = scheduleCardLocally(d, 21, '2026-11-25', '2026-11-25');
    expect(placed.rows.at(-1)).toMatchObject({ id: 21, dueDate: '2026-11-25' });
    expect(placed.unscheduled.some((c) => c.id === 21)).toBe(false);
    expect(placed.unscheduledTotal).toBe(1);
    expect(scheduleCardLocally(d, 999, null, null).rows).toEqual(d.rows);
  });

  it('伺服器回的卡片取代畫面上的那一張（含時間窗外的父任務列）', () => {
    const d = data({ parents: [{ id: 50, title: 'p', visible: true, row: cardWire({ id: 50, dueDate: '2026-10-01' }) }] });
    const card = { ...d.parents[0].row!, version: 'v2' };
    expect(replaceCardLocally(d, card).parents[0].row?.version).toBe('v2');
    const row = { ...d.rows[0], version: 'v3' };
    expect(replaceCardLocally(d, row).rows[0].version).toBe('v3');
  });
});
