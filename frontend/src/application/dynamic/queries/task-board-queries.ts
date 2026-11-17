import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import {
  moveCardLocally, removeCardLocally, type BoardCard, type BoardColumn, type BoardGroupBy, type BoardSource, type TaskBoardData, type TaskMoveResult,
} from 'src/domain/dynamic/task-board-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { invalidateTasks } from './tasks-queries';

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

export interface MoveTaskVars {
  card: BoardCard;
  groupBy: BoardGroupBy;
  toColumn: string;
  /** 只有移到「之後」欄時帶。 */
  dueDate?: string;
}

interface MoveContext { key: ReturnType<typeof boardKey>; snapshot: TaskBoardData | undefined }

/**
 * 移動任務（拖拉或「移到…」）。onMutate 先把卡片搬到目標欄；失敗時放回原樣，再重抓看板（409 也一樣，由呼叫端提示）。
 * 快取鍵在 mutate 當下固定，請求途中換看板也不會寫錯。伺服器回的卡片取代樂觀版本；回 visible=false（改負責人後看不到）就從畫面拿掉，提示由呼叫端顯示。
 */
export function useMoveTask(source: MaybeRefOrGetter<BoardSource>) {
  const qc = useQueryClient();
  return useMutation<TaskMoveResult, unknown, MoveTaskVars, MoveContext>({
    mutationFn: (v) => dynamicApi.moveTask(v.card.id, {
      groupBy: v.groupBy, toColumn: v.toColumn, expectedVersion: v.card.version, ...(v.dueDate ? { dueDate: v.dueDate } : {}),
    }),
    onMutate: async (v) => {
      const key = boardKey(toValue(source));
      await qc.cancelQueries({ queryKey: key });
      const snapshot = qc.getQueryData<TaskBoardData>(key);
      const next = snapshot && moveCardLocally(snapshot, v.card.id, v.toColumn, v.groupBy, v.dueDate);
      if (next) qc.setQueryData<TaskBoardData>(key, next);
      return { key, snapshot };
    },
    onSuccess: (result, v, ctx) => {
      qc.setQueryData<TaskBoardData>(ctx.key, (board) => {
        if (!board) return board;
        if (!result.visible || !result.card) return result.visible ? board : removeCardLocally(board, v.card.id);
        const card = result.card;
        return { ...board, columns: board.columns.map((c) => ({ ...c, cards: c.cards.map((x) => (x.id === card.id ? card : x)) })) };
      });
      void invalidateTasks(qc, v.card.id);
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.snapshot) qc.setQueryData<TaskBoardData>(ctx.key, ctx.snapshot);
      void qc.invalidateQueries({ queryKey: ['task-board'] });
    },
  });
}

/** 我的看板與分享給我的看板；Task 10 的選擇器使用。 */
export function useTaskViews(enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({ queryKey: ['task-views', 'board'], queryFn: () => dynamicApi.getTaskViews('board'), enabled: computed(() => toValue(enabled)) });
}
