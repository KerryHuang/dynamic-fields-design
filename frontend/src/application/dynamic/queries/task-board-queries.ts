import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { BoardColumn, BoardSource, TaskBoardData } from 'src/domain/dynamic/task-board-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** `invalidateTasks`（tasks-queries）以 `['task-board']` 前綴一併失效；任務有任何變動，看板欄跟著重抓。 */
export const boardKey = (source: BoardSource) => ['task-board', 'columns', source.viewId ?? null, source.config ?? null] as const;

/** 看板各欄的總數與第一頁卡片。 */
export function useBoardColumns(source: MaybeRefOrGetter<BoardSource>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => boardKey(toValue(source))),
    queryFn: () => dynamicApi.getBoardColumns(toValue(source)),
    enabled: computed(() => toValue(enabled)),
  });
}

/** 把下一頁接在該欄卡片後面（以 id 去重）；成功後直接改快取，不重抓整個看板。快取鍵在發請求時就固定，請求途中換看板也不會寫錯。 */
export function useLoadMoreColumn(source: MaybeRefOrGetter<BoardSource>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (column: BoardColumn) => {
      const s = toValue(source);
      return { key: boardKey(s), page: await dynamicApi.getBoardColumnPage(column.key, s, column.cards.length) };
    },
    onSuccess: ({ key, page }) => {
      qc.setQueryData<TaskBoardData>(key, (board) => board && {
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
