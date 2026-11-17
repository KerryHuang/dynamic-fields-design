import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { BoardColumn, BoardSource, TaskBoardData } from 'src/domain/dynamic/task-board-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

/** `invalidateTasks`（tasks-queries）以 `['task-board']` 前綴一併失效；任務有任何變動，看板欄跟著重抓。 */
export const boardKey = (source: BoardSource) => ['task-board', 'columns', source.viewId ?? null, source.config ?? null] as const;

/** 404（已刪除）與 403（不再分享）是 D6 的回預設看板情境，重試也不會好；其他失敗重試一次。 */
const retryBoard = (count: number, e: unknown) => ![403, 404].includes((e as ApiError)?.status) && count < 1;

/** 看板各欄的總數與第一頁卡片。 */
export function useBoardColumns(source: MaybeRefOrGetter<BoardSource>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => boardKey(toValue(source))),
    queryFn: () => dynamicApi.getBoardColumns(toValue(source)),
    enabled: computed(() => toValue(enabled)),
    retry: retryBoard,
  });
}

/** 把下一頁接在該欄卡片後面（以 id 去重）；成功後直接改快取，不重抓整個看板。 */
export function useLoadMoreColumn(source: MaybeRefOrGetter<BoardSource>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (column: BoardColumn) => dynamicApi.getBoardColumnPage(column.key, toValue(source), column.cards.length),
    onSuccess: (page) => {
      qc.setQueryData<TaskBoardData>(boardKey(toValue(source)), (board) => board && {
        ...board,
        columns: board.columns.map((c) => {
          if (c.key !== page.key) return c;
          const seen = new Set(c.cards.map((x) => x.id));
          return { ...c, total: page.total, cards: [...c.cards, ...page.cards.filter((x) => !seen.has(x.id))], hasMore: page.hasMore, truncated: page.truncated };
        }),
      });
    },
  });
}

/** 我的看板與分享給我的看板；Task 10 的選擇器使用。 */
export function useTaskViews(enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({ queryKey: ['task-views', 'board'], queryFn: () => dynamicApi.getTaskViews('board'), enabled: computed(() => toValue(enabled)) });
}
