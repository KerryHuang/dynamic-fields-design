import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import {
  moveCardLocally, removeCardLocally, restoreCardLocally, type BoardCard, type BoardColumn, type BoardGroupBy, type BoardSource, type TaskBoardConfig, type TaskBoardData, type TaskMoveResult, type TaskViewShareKind,
} from 'src/domain/dynamic/task-board-types';
import type { TaskCascade } from 'src/domain/dynamic/task-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
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
  /** 把有未完成子任務的父任務拖到完成欄、使用者選了連動方式後重送時帶（版本與第一次相同）。 */
  cascade?: TaskCascade;
}

interface MoveContext { key: ReturnType<typeof boardKey>; original: BoardCard | undefined; fromKey: string | null; fromIndex: number }

const MOVE_KEY = ['task-board', 'move'] as const;

/**
 * 移動任務（拖拉或「移到…」）。onMutate 先把卡片搬到目標欄；失敗時只把這一張卡放回原位（其他移動可能還在途中，不能用整份快照蓋掉）。
 * 快取鍵在 mutate 當下固定，請求途中換看板也不會寫錯。伺服器回的卡片取代樂觀版本；回 visible=false（改負責人後看不到）就從畫面拿掉，提示由呼叫端顯示。
 * 看板重抓要等最後一個進行中的移動結束，否則重抓結果還沒有其他卡的改動，那些卡會先跳回原欄。
 */
export function useMoveTask(source: MaybeRefOrGetter<BoardSource>) {
  const qc = useQueryClient();
  return useMutation<TaskMoveResult, unknown, MoveTaskVars, MoveContext>({
    mutationKey: MOVE_KEY,
    mutationFn: (v) => dynamicApi.moveTask(v.card.id, {
      groupBy: v.groupBy, toColumn: v.toColumn, expectedVersion: v.card.version, ...(v.dueDate ? { dueDate: v.dueDate } : {}),
      ...(v.cascade ? { cascade: v.cascade } : {}),
    }),
    onMutate: async (v) => {
      const key = boardKey(toValue(source));
      await qc.cancelQueries({ queryKey: key });
      const board = qc.getQueryData<TaskBoardData>(key);
      const from = board?.columns.find((c) => c.cards.some((x) => x.id === v.card.id));
      const next = board && moveCardLocally(board, v.card.id, v.toColumn, v.groupBy, v.dueDate);
      if (next) qc.setQueryData<TaskBoardData>(key, next);
      return { key, original: from?.cards.find((x) => x.id === v.card.id), fromKey: from?.key ?? null, fromIndex: from?.cards.findIndex((x) => x.id === v.card.id) ?? 0 };
    },
    onSuccess: (result, v, ctx) => {
      qc.setQueryData<TaskBoardData>(ctx.key, (board) => {
        if (!board) return board;
        if (!result.visible || !result.card) return result.visible ? board : removeCardLocally(board, v.card.id);
        const card = result.card;
        return { ...board, columns: board.columns.map((c) => ({ ...c, cards: c.cards.map((x) => (x.id === card.id ? card : x)) })) };
      });
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.original && ctx.fromKey !== null) {
        const { original, fromKey, fromIndex } = ctx;
        qc.setQueryData<TaskBoardData>(ctx.key, (board) => board && restoreCardLocally(board, original, fromKey, fromIndex));
      }
    },
    onSettled: (result, error, v) => {
      // 自己還算在進行中的數量裡；只有最後一個結束的移動才重抓看板
      const last = qc.isMutating({ mutationKey: MOVE_KEY }) <= 1;
      if (error) {
        if (last) void qc.invalidateQueries({ queryKey: ['task-board'] });
      } else {
        void invalidateTasks(qc, v.card.id, { board: last });
      }
    },
  });
}

/** 我的看板與分享給我的看板（選擇器用）。 */
export function useTaskViews(enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({ queryKey: ['task-views', 'board'], queryFn: () => dynamicApi.getTaskViews('board'), enabled: computed(() => toValue(enabled)) });
}

/** 一個存檔看板的設定、權限與（擁有者／管理員才有的）分享對象。 */
export function useTaskView(id: MaybeRefOrGetter<number | null>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['task-view', toValue(id)]),
    queryFn: () => dynamicApi.getTaskView(toValue(id)!),
    enabled: computed(() => toValue(id) !== null && toValue(enabled)),
  });
}

/** 分享對話框的選項：使用者、部門、群組；任何登入者都讀得到。 */
export function useTaskViewShareTargets(enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({ queryKey: ['task-view-share-targets'], queryFn: () => dynamicApi.getTaskViewShareTargets(), enabled: computed(() => toValue(enabled)) });
}

/** 另存為新看板；清單失效，由呼叫端決定要不要切過去。 */
export function useCreateTaskView() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { name: string; config: TaskBoardConfig }) => dynamicApi.createTaskView({ kind: 'board', ...v }),
    onSuccess: (detail) => {
      qc.setQueryData(['task-view', detail.id], detail);
      return qc.invalidateQueries({ queryKey: ['task-views'] });
    },
  });
}

const statusOf = (e: unknown) => (e as ApiError | null)?.status;

/**
 * 看板在別處被改了（409）或已被刪掉（404）：頁面一直掛著這個看板的查詢、全域又關掉視窗焦點重抓，不主動失效的話
 * 重開設定拿到的還是舊的 rowVersion，會一直撞 409。404 失效後欄查詢回 404，頁面走 D6 回預設看板。
 */
function refreshStaleView(qc: ReturnType<typeof useQueryClient>, id: number, e: unknown) {
  const status = statusOf(e);
  if (status !== 409 && status !== 404) return;
  void qc.invalidateQueries({ queryKey: ['task-view', id] });
  void qc.invalidateQueries({ queryKey: ['task-board'] });
  void qc.invalidateQueries({ queryKey: ['task-views'] });
}

/** 儲存目前看板（名稱與設定）；rowVersion 不符回 409。成功後該看板的欄資料重抓。 */
export function useUpdateTaskView() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: number; name: string; config: TaskBoardConfig; rowVersion: string }) =>
      dynamicApi.updateTaskView(v.id, { name: v.name, config: v.config, rowVersion: v.rowVersion }),
    onSuccess: (detail) => {
      qc.setQueryData(['task-view', detail.id], detail);
      void qc.invalidateQueries({ queryKey: ['task-board'] });
      return qc.invalidateQueries({ queryKey: ['task-views'] });
    },
    onError: (e, v) => refreshStaleView(qc, v.id, e),
  });
}

export function useDeleteTaskView() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.deleteTaskView(id),
    onSuccess: (_r, id) => {
      qc.removeQueries({ queryKey: ['task-view', id] });
      qc.removeQueries({ queryKey: ['task-board', 'columns', id] });
      return qc.invalidateQueries({ queryKey: ['task-views'] });
    },
    // 已被別人刪掉：清單重抓（呼叫端把它當成已刪除）
    onError: (e) => { if (statusOf(e) === 404) void qc.invalidateQueries({ queryKey: ['task-views'] }); },
  });
}

/** 整批取代分享對象；id 一律是字串（使用者帳號，或部門、群組的數字 id 轉字串）。 */
export function useSetTaskViewShares() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: number; shares: { kind: TaskViewShareKind; id: string }[] }) => dynamicApi.setTaskViewShares(v.id, v.shares),
    onSuccess: (detail) => {
      qc.setQueryData(['task-view', detail.id], detail);
      return qc.invalidateQueries({ queryKey: ['task-views'] });
    },
    onError: (e, v) => refreshStaleView(qc, v.id, e),
  });
}
