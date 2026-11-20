import { describe, expect, it } from 'vitest';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import {
  BOARD_COLUMN_GAP, BOARD_MIN_COLUMN_WIDTH, columnsPerScreen, gridTrackCount, isLaterDateAllowed, moveBlockKey, moveFlowOf, moveTargets, moveCardLocally, removeCardLocally, restoreCardLocally,
  visibleRange, type BoardCard, type BoardColumn, type TaskBoardData,
} from './task-board-types';

const card = (o: Partial<BoardCard> = {}): BoardCard => ({
  id: 1, title: 'T', status: 'todo', assigneeUserId: 'sales01', assigneeName: '業務一', assigneeActive: true, dueDate: null, startDate: null,
  overdue: false, dueToday: false, record: null, fromRule: false, frozen: false, commentCount: 0, completedTime: null, version: 'v1', canEdit: true, canReassign: true,
  parentTaskId: null, parentTitle: null, level: 'standalone', progress: null, ...o,
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
    const locales = [zhTW, (await import('shared/i18n/en-US/ui.json')).default, (await import('shared/i18n/th/ui.json')).default, (await import('shared/i18n/vi/ui.json')).default];
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

  it('moveTargets lets an assignee board move a card the user can edit even without the reassign right', () => {
    const columns = [column({ key: 'unassigned', kind: 'unassigned' }), column({ key: 'user:a', kind: 'user' })];
    expect(moveTargets(card({ canEdit: true, canReassign: false }), columns, 'assignee').every((t) => t.droppable)).toBe(true);
    expect(moveTargets(card({ canEdit: false, canReassign: false }), columns, 'assignee').every((t) => t.reason?.kind === 'noPermission')).toBe(true);
  });

  it('moveFlowOf picks the confirmation each drop needs', () => {
    const cancelled = column({ key: 'cancelled' });
    const none = column({ key: 'none', kind: 'dueBucket' });
    expect(moveFlowOf(card(), cancelled, 'status')).toBe('confirmCancel');
    expect(moveFlowOf(card(), column({ key: 'done' }), 'status')).toBe('direct');
    expect(moveFlowOf(card({ fromRule: true }), none, 'dueBucket')).toBe('confirmClearRuleDue');
    expect(moveFlowOf(card({ fromRule: false }), none, 'dueBucket')).toBe('direct');
    expect(moveFlowOf(card(), column({ key: 'later', kind: 'dueBucket' }), 'dueBucket')).toBe('pickDate');
    expect(moveFlowOf(card({ fromRule: true }), column({ key: 'user:a', kind: 'user' }), 'assignee')).toBe('direct');
  });

  it('isLaterDateAllowed accepts only dates after the end of the week', () => {
    expect(isLaterDateAllowed('2026-11-22', '2026-11-22')).toBe(false);
    expect(isLaterDateAllowed('2026-11-21', '2026-11-22')).toBe(false);
    expect(isLaterDateAllowed('2026-11-23', '2026-11-22')).toBe(true);
    expect(isLaterDateAllowed('', '2026-11-22')).toBe(false);
    expect(isLaterDateAllowed(null, '2026-11-22')).toBe(false);
  });

  const board = (): TaskBoardData => ({
    today: '2026-11-17', weekEnd: '2026-11-22', laterDefault: '2026-11-29', scopeAvailable: true, omittedAssignees: 0,
    config: { version: 1, groupBy: 'status', scope: 'assigned' },
    columns: [
      column({ key: 'todo', total: 2, cards: [card({ id: 1 }), card({ id: 2 })] }),
      column({ key: 'done', total: 0 }),
    ],
  });

  it('moveCardLocally moves the card to the top of the target column, adjusts both totals and the card fields', () => {
    const next = moveCardLocally(board(), 1, 'done', 'status', undefined)!;
    expect(next.columns.map((c) => [c.key, c.total, c.cards.map((x) => x.id)])).toEqual([['todo', 1, [2]], ['done', 1, [1]]]);
    expect(next.columns[1].cards[0].status).toBe('done');
    // 原本的看板資料不被改動
    expect(board().columns[0].cards).toHaveLength(2);
  });

  it('moveCardLocally rewrites the due date or the assignee for the other groupings', () => {
    const b = board();
    b.columns = [
      column({ key: 'today', kind: 'dueBucket', cards: [card({ id: 1, dueDate: '2026-11-17', dueToday: true })], total: 1 }),
      column({ key: 'later', kind: 'dueBucket' }), column({ key: 'none', kind: 'dueBucket' }),
    ];
    expect(moveCardLocally(b, 1, 'later', 'dueBucket', '2026-12-01')!.columns[1].cards[0]).toMatchObject({ dueDate: '2026-12-01', dueToday: false, overdue: false });
    expect(moveCardLocally(b, 1, 'none', 'dueBucket', undefined)!.columns[2].cards[0].dueDate).toBeNull();
    const a = board();
    a.columns = [column({ key: 'unassigned', kind: 'unassigned', cards: [card({ id: 1 })], total: 1 }), column({ key: 'user:bob', kind: 'user', userId: 'bob', name: '小波', inactive: false })];
    expect(moveCardLocally(a, 1, 'user:bob', 'assignee', undefined)!.columns[1].cards[0]).toMatchObject({ assigneeUserId: 'bob', assigneeName: '小波', assigneeActive: true });
    const back = moveCardLocally(a, 1, 'user:bob', 'assignee', undefined)!;
    expect(moveCardLocally(back, 1, 'unassigned', 'assignee', undefined)!.columns[0].cards[0]).toMatchObject({ assigneeUserId: null, assigneeName: null });
  });

  it('moveCardLocally returns null when the card or the column is not on the loaded board; removeCardLocally drops it', () => {
    expect(moveCardLocally(board(), 99, 'done', 'status', undefined)).toBeNull();
    expect(moveCardLocally(board(), 1, 'nope', 'status', undefined)).toBeNull();
    const next = removeCardLocally(board(), 1);
    expect(next.columns[0]).toMatchObject({ total: 1 });
    expect(next.columns[0].cards.map((x) => x.id)).toEqual([2]);
  });

  it('restoreCardLocally puts the original card back at its place and touches nothing else', () => {
    const original = board().columns[0].cards[0];
    const other = moveCardLocally(board(), 2, 'done', 'status', undefined)!;
    const both = moveCardLocally(other, 1, 'done', 'status', undefined)!;
    const back = restoreCardLocally(both, original, 'todo', 0);
    // card 2 stays in done (its own move is still in flight); card 1 is the one that went back, with its original fields
    expect(back.columns.map((c) => [c.key, c.total, c.cards.map((x) => x.id)])).toEqual([['todo', 1, [1]], ['done', 1, [2]]]);
    expect(back.columns[0].cards[0]).toBe(original);
  });

  it('restoreCardLocally leaves the board alone when the card is already home, gone, or its home column is gone', () => {
    const original = board().columns[0].cards[0];
    const b = board();
    expect(restoreCardLocally(b, original, 'todo', 0)).toBe(b);
    expect(restoreCardLocally(removeCardLocally(b, 1), original, 'todo', 0).columns[0].cards.map((x) => x.id)).toEqual([2]);
    const moved = moveCardLocally(b, 1, 'done', 'status', undefined)!;
    expect(restoreCardLocally(moved, original, 'gone', 0)).toBe(moved);
  });
});
