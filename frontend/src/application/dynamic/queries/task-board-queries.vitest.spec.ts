import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent, ref } from 'vue';
import type { BoardColumnPage, BoardSource, TaskBoardData } from 'src/domain/dynamic/task-board-types';
import { boardWire, cardWire, columnWire } from 'src/presentation/tasks/board/board-fixtures';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { normalizeBoard, normalizeBoardPage } from 'src/services/api/task-normalize';
import { boardKey, useLoadMoreColumn, useMoveTask } from './task-board-queries';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getBoardColumnPage: vi.fn(), moveTask: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const board = (cardId: number) => normalizeBoard(boardWire({ columns: [columnWire({ key: 'todo', total: 60, hasMore: true, cards: [cardWire({ id: cardId })] })] }) as never);
const ids = (qc: QueryClient, source: BoardSource) => qc.getQueryData<TaskBoardData>(boardKey(source))!.columns[0].cards.map((c) => c.id);

describe('useLoadMoreColumn', () => {
  it('writes the page into the board it was requested for, even if the board changed while the request was out', async () => {
    const qc = new QueryClient();
    qc.setQueryData(boardKey({ viewId: 1 }), board(1));
    qc.setQueryData(boardKey({ viewId: 2 }), board(2));
    const source = ref<BoardSource>({ viewId: 1 });
    let loadMore!: ReturnType<typeof useLoadMoreColumn>;
    mount(defineComponent({ setup() { loadMore = useLoadMoreColumn(source); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
    let answer!: (p: BoardColumnPage) => void;
    api.getBoardColumnPage.mockReturnValue(new Promise<BoardColumnPage>((r) => { answer = r; }));

    const done = loadMore.mutateAsync(qc.getQueryData<TaskBoardData>(boardKey({ viewId: 1 }))!.columns[0]);
    await flushPromises();
    expect(api.getBoardColumnPage).toHaveBeenCalledWith('todo', { viewId: 1 }, 1);
    source.value = { viewId: 2 };
    answer(normalizeBoardPage({ key: 'todo', total: 60, cards: [cardWire({ id: 99 })], hasMore: false, truncated: false } as never));
    await done;

    expect(ids(qc, { viewId: 1 })).toEqual([1, 99]);
    expect(ids(qc, { viewId: 2 })).toEqual([2]);
  });
});

describe('useMoveTask', () => {
  const twoColumns = (cardId: number) => normalizeBoard(boardWire({ columns: [
    columnWire({ key: 'todo', total: 1, cards: [cardWire({ id: cardId })] }), columnWire({ key: 'done', total: 0, cards: [] }),
  ] }) as never);
  const column = (qc: QueryClient, source: BoardSource, key: string) => qc.getQueryData<TaskBoardData>(boardKey(source))!.columns.find((c) => c.key === key)!;

  function mountMove(qc: QueryClient, source: { value: BoardSource }) {
    let move!: ReturnType<typeof useMoveTask>;
    mount(defineComponent({ setup() { move = useMoveTask(() => source.value); return () => null; } }), { global: { plugins: [[VueQueryPlugin, { queryClient: qc }]] } });
    return () => move;
  }
  const vars = (qc: QueryClient, source: BoardSource) => ({ card: column(qc, source, 'todo').cards[0], groupBy: 'status' as const, toColumn: 'done' });

  it('moves the card in the cache before the server answers and keeps the server card afterwards', async () => {
    const qc = new QueryClient();
    qc.setQueryData(boardKey({ viewId: 1 }), twoColumns(1));
    const move = mountMove(qc, ref<BoardSource>({ viewId: 1 }));
    let answer!: (r: unknown) => void;
    api.moveTask.mockReturnValue(new Promise((r) => { answer = r; }) as never);
    const done = move().mutateAsync(vars(qc, { viewId: 1 }));
    await flushPromises();
    expect(column(qc, { viewId: 1 }, 'done').cards.map((c) => c.id)).toEqual([1]);
    expect(column(qc, { viewId: 1 }, 'todo').total).toBe(0);
    expect(api.moveTask).toHaveBeenCalledWith(1, { groupBy: 'status', toColumn: 'done', expectedVersion: '20261101020000000' });
    answer({ visible: true, card: { ...cardWire({ id: 1, status: 'done', version: 'v2' }), assigneeActive: true, record: null }, undoToken: null });
    await done;
    expect(column(qc, { viewId: 1 }, 'done').cards[0].version).toBe('v2');
  });

  it('puts the card back into the board it was moved on when the request fails after the board was switched', async () => {
    const qc = new QueryClient();
    qc.setQueryData(boardKey({ viewId: 1 }), twoColumns(1));
    qc.setQueryData(boardKey({ viewId: 2 }), twoColumns(2));
    const source = ref<BoardSource>({ viewId: 1 });
    const move = mountMove(qc, source);
    let fail!: (e: unknown) => void;
    api.moveTask.mockReturnValue(new Promise((_, r) => { fail = r; }) as never);
    const done = move().mutateAsync(vars(qc, { viewId: 1 })).catch((e) => e);
    await flushPromises();
    source.value = { viewId: 2 };
    fail({ status: 400 });
    await done;
    expect(column(qc, { viewId: 1 }, 'todo').cards.map((c) => c.id)).toEqual([1]);
    expect(column(qc, { viewId: 2 }, 'todo').cards.map((c) => c.id)).toEqual([2]);
    expect(column(qc, { viewId: 2 }, 'done').cards).toHaveLength(0);
  });

  it('drops the card from the board when the server says it is no longer visible', async () => {
    const qc = new QueryClient();
    qc.setQueryData(boardKey({ viewId: 1 }), twoColumns(1));
    const move = mountMove(qc, ref<BoardSource>({ viewId: 1 }));
    api.moveTask.mockResolvedValue({ visible: false, card: null, undoToken: 'tok' });
    await move().mutateAsync(vars(qc, { viewId: 1 }));
    expect(qc.getQueryData<TaskBoardData>(boardKey({ viewId: 1 }))!.columns.flatMap((c) => c.cards)).toHaveLength(0);
  });

  describe('two moves in flight at once', () => {
    const threeCards = () => normalizeBoard(boardWire({ columns: [
      columnWire({ key: 'todo', total: 3, cards: [cardWire({ id: 1 }), cardWire({ id: 2 }), cardWire({ id: 3 })] }), columnWire({ key: 'done', total: 0, cards: [] }),
    ] }) as never);
    const idsOf = (qc: QueryClient, key: string) => column(qc, { viewId: 1 }, key).cards.map((c) => c.id);
    const boardInvalidations = (spy: { mock: { calls: unknown[][] } }) => spy.mock.calls
      .filter((c) => JSON.stringify((c[0] as { queryKey?: unknown })?.queryKey) === JSON.stringify(['task-board'])).length;

    function start() {
      const qc = new QueryClient();
      qc.setQueryData(boardKey({ viewId: 1 }), threeCards());
      const move = mountMove(qc, ref<BoardSource>({ viewId: 1 }));
      const answers: Record<number, { ok: (r: unknown) => void; fail: (e: unknown) => void }> = {};
      api.moveTask.mockImplementation(((id: number) => new Promise((ok, fail) => { answers[id] = { ok, fail }; })) as never);
      const run = (id: number) => move().mutateAsync({ card: column(qc, { viewId: 1 }, 'todo').cards.find((c) => c.id === id)!, groupBy: 'status', toColumn: 'done' }).catch((e) => e);
      return { qc, answers, run, spy: vi.spyOn(qc, 'invalidateQueries') };
    }

    it('a failed move puts back only its own card; the other card keeps its optimistic place until it is answered', async () => {
      const { qc, answers, run } = start();
      const a = run(1);
      const b = run(3);
      await flushPromises();
      expect(idsOf(qc, 'done')).toEqual([3, 1]);
      answers[1].fail({ status: 400 });
      await a;
      // A is back where it was (first in todo); B is still in done
      expect(idsOf(qc, 'todo')).toEqual([1, 2]);
      expect(idsOf(qc, 'done')).toEqual([3]);
      expect(column(qc, { viewId: 1 }, 'todo').total).toBe(2);
      expect(column(qc, { viewId: 1 }, 'done').total).toBe(1);
      answers[3].ok({ visible: true, card: { ...cardWire({ id: 3, status: 'done', version: 'v3' }), assigneeActive: true, record: null }, undoToken: null });
      await b;
      expect(idsOf(qc, 'done')).toEqual([3]);
      expect(column(qc, { viewId: 1 }, 'done').cards[0].version).toBe('v3');
    });

    it('the board is refetched only once the last move in flight has settled', async () => {
      const { qc, answers, run, spy } = start();
      const a = run(1);
      const b = run(3);
      await flushPromises();
      answers[1].ok({ visible: true, card: { ...cardWire({ id: 1, status: 'done', version: 'v2' }), assigneeActive: true, record: null }, undoToken: null });
      await a;
      await flushPromises();
      expect(boardInvalidations(spy)).toBe(0);
      // the task lists and counters still refresh right away
      expect(spy.mock.calls.some((c) => JSON.stringify((c[0] as { queryKey?: unknown })?.queryKey) === JSON.stringify(['tasks']))).toBe(true);
      expect(idsOf(qc, 'done')).toEqual([3, 1]);
      answers[3].fail({ status: 409, code: 'stale' });
      await b;
      await flushPromises();
      expect(boardInvalidations(spy)).toBe(1);
    });

    it('a single move still refetches the board when it fails', async () => {
      const { answers, run, spy } = start();
      const a = run(1);
      await flushPromises();
      answers[1].fail({ status: 500 });
      await a;
      await flushPromises();
      expect(boardInvalidations(spy)).toBe(1);
    });
  });
});

