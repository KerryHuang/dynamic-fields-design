import { describe, expect, it } from 'vitest';
import zhTW from 'src/i18n/zh-TW.json';
import { BOARD_COLUMN_GAP, BOARD_MIN_COLUMN_WIDTH, columnsPerScreen, groupByOf, gridTrackCount, moveBlockKey, moveTargets, visibleRange, type BoardCard, type BoardColumn } from './task-board-types';

const card = (o: Partial<BoardCard> = {}): BoardCard => ({
  id: 1, title: 'T', status: 'todo', assigneeUserId: 'sales01', assigneeName: '業務一', assigneeActive: true, dueDate: null, overdue: false,
  dueToday: false, record: null, fromRule: false, frozen: false, commentCount: 0, completedTime: null, version: 'v1', canEdit: true, canReassign: true, ...o,
});
const column = (o: Partial<BoardColumn> = {}): BoardColumn => ({
  key: 'todo', kind: 'status', userId: null, name: null, inactive: false, total: 0, cards: [], hasMore: false, truncated: false,
  droppable: true, ...o,
});

describe('task-board-types', () => {
  it('columnsPerScreen gives 1 on phone and fits four status columns at 1366', () => {
    expect(columnsPerScreen(390 - 32)).toBe(1);
    expect(columnsPerScreen(0)).toBe(1);
    // 820 寬的平板：內容寬 788，放得下三欄狀態欄
    expect(columnsPerScreen(820 - 32)).toBe(3);
    // 1366 寬：扣掉最寬的側欄 208 與頁面左右 padding 32
    expect(columnsPerScreen(1366 - 208 - 32)).toBe(4);
    expect(columnsPerScreen(1920 - 208 - 32)).toBeGreaterThanOrEqual(4);
  });

  it('every perScreen it returns fits its columns inside the width', () => {
    for (const w of [1, 239, 240, 251, 252, 358, 788, 1126, 1680]) {
      const n = columnsPerScreen(w);
      expect(n * BOARD_MIN_COLUMN_WIDTH + (n - 1) * BOARD_COLUMN_GAP <= Math.max(w, BOARD_MIN_COLUMN_WIDTH)).toBe(true);
    }
    expect(BOARD_MIN_COLUMN_WIDTH).toBe(240);
  });

  it('visibleRange clamps at the end', () => {
    expect(visibleRange(0, 4, 9)).toEqual({ page: 0, pageCount: 3, start: 0, end: 4 });
    expect(visibleRange(1, 4, 9)).toEqual({ page: 1, pageCount: 3, start: 4, end: 8 });
    expect(visibleRange(2, 4, 9)).toEqual({ page: 2, pageCount: 3, start: 8, end: 9 });
    expect(visibleRange(9, 4, 9)).toEqual({ page: 2, pageCount: 3, start: 8, end: 9 });
    expect(visibleRange(-3, 4, 9).page).toBe(0);
    expect(visibleRange(0, 4, 0)).toEqual({ page: 0, pageCount: 1, start: 0, end: 0 });
    expect(visibleRange(0, 4, 3)).toEqual({ page: 0, pageCount: 1, start: 0, end: 3 });
  });

  it('groupByOf reads the grouping from the column kinds', () => {
    expect(groupByOf([column({ kind: 'status' })])).toBe('status');
    expect(groupByOf([column({ kind: 'dueBucket' })])).toBe('dueBucket');
    expect(groupByOf([column({ kind: 'unassigned' })])).toBe('assignee');
    expect(groupByOf([column({ kind: 'user' })])).toBe('assignee');
    expect(groupByOf([])).toBe('status');
  });

  it('moveTargets marks overdue and thisWeek-on-weekend as not droppable with a reason', () => {
    const columns = [
      column({ key: 'overdue', kind: 'dueBucket', droppable: false }),
      column({ key: 'today', kind: 'dueBucket', cards: [card({ id: 1 })] }),
      column({ key: 'thisWeek', kind: 'dueBucket', droppable: false }),
      column({ key: 'later', kind: 'dueBucket' }),
    ];
    const targets = moveTargets(card({ id: 1 }), columns, 'dueBucket');
    expect(targets.map((t) => [t.key, t.droppable, t.current])).toEqual([
      ['overdue', false, false], ['today', true, true], ['thisWeek', false, false], ['later', true, false],
    ]);
    expect(targets[0].reason).toEqual({ kind: 'column', reason: 'overdue' });
    expect(targets[2].reason).toEqual({ kind: 'column', reason: 'thisWeekEmpty' });
    expect(targets[3].reason).toBeNull();
  });

  it('moveTargets derives the reason from the column, never from server text; an inactive assignee column says so', () => {
    const columns = [
      column({ key: 'user:old01', kind: 'user', inactive: true, droppable: false }),
      column({ key: 'later', kind: 'dueBucket', droppable: false }),
    ];
    const targets = moveTargets(card(), columns, 'assignee');
    expect(targets[0].reason).toEqual({ kind: 'column', reason: 'inactiveAssignee' });
    expect(targets[1].reason).toEqual({ kind: 'column', reason: 'unavailable' });
  });

  it('every move block has a message in all four locales (taskBoard.drop.*)', async () => {
    const blocks = [
      { kind: 'column', reason: 'overdue' }, { kind: 'column', reason: 'thisWeekEmpty' }, { kind: 'column', reason: 'inactiveAssignee' },
      { kind: 'column', reason: 'unavailable' }, { kind: 'noPermission' }, { kind: 'frozen' },
    ] as const;
    const locales = [zhTW, (await import('src/i18n/en-US.json')).default, (await import('src/i18n/th.json')).default, (await import('src/i18n/vi.json')).default];
    for (const b of blocks) {
      for (const m of locales) {
        const text = moveBlockKey(b).split('.').reduce<unknown>((o, k) => (o as Record<string, unknown>)?.[k], m);
        expect(typeof text, moveBlockKey(b)).toBe('string');
      }
    }
  });

  it('gridTrackCount fills the width when every column fits and keeps the fixed count when it has to page', () => {
    expect(gridTrackCount(3, 4)).toBe(3);
    expect(gridTrackCount(3, 3)).toBe(3);
    expect(gridTrackCount(9, 4)).toBe(4);
    expect(gridTrackCount(1, 1)).toBe(1);
  });

  it('moveTargets blocks every column for a frozen card or a missing right, depending on the grouping', () => {
    const columns = [column({ key: 'todo' }), column({ key: 'done' })];
    expect(moveTargets(card({ frozen: true }), columns, 'status').every((t) => t.reason?.kind === 'frozen')).toBe(true);
    // 主管只能改負責人：依狀態分欄不能動，依負責人分欄可以
    const supervised = card({ canEdit: false, canReassign: true });
    expect(moveTargets(supervised, columns, 'status').every((t) => t.reason?.kind === 'noPermission')).toBe(true);
    expect(moveTargets(supervised, columns, 'assignee').every((t) => t.droppable)).toBe(true);
  });
});
