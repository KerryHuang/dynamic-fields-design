import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent, ref } from 'vue';
import type { BoardColumnPage, BoardSource, TaskBoardData } from 'src/domain/dynamic/task-board-types';
import { boardWire, cardWire, columnWire } from 'src/presentation/tasks/board/board-fixtures';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { normalizeBoard, normalizeBoardPage } from 'src/services/api/task-normalize';
import { boardKey, useLoadMoreColumn } from './task-board-queries';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getBoardColumnPage: vi.fn() } }));
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
