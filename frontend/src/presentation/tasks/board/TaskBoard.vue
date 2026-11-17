<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useLoadMoreColumn, useMoveTask } from 'src/application/dynamic/queries/task-board-queries';
import {
  BOARD_COLUMN_GAP, columnsPerScreen, gridTrackCount, groupByOf, moveBlockKey, moveFlowOf, moveTargets, visibleRange,
  type BoardCard, type BoardColumn, type BoardSource, type ColumnDrop, type MoveFlow, type TaskBoardData,
} from 'src/domain/dynamic/task-board-types';
import { useMovedOutNotice } from 'src/presentation/dynamic/tasks/use-moved-out-notice';
import type { ApiError } from 'src/services/api/http';
import BoardColumnPager from './BoardColumnPager.vue';
import DuePickDialog from './DuePickDialog.vue';
import MoveConfirmDialog from './MoveConfirmDialog.vue';
import TaskBoardColumn from './TaskBoardColumn.vue';
import { useCardDrag } from './use-card-drag';
import { useElementWidth } from './use-element-width';

/**
 * 看板：欄依容器寬度分段顯示（D1），不換行也不橫向捲動；全部欄一屏放得下時撐滿寬度，要分段時固定一屏欄數。
 * `source` 用來把「載入更多」與移動的樂觀更新接回同一份快取。移動有兩條路：拖拉（一屏有多欄才啟用，D2）與每張卡的「移到…」，
 * 兩條都走 requestMove；結果用唯一的 aria-live 念出。
 */
const props = defineProps<{ board: TaskBoardData; source: BoardSource }>();
const emit = defineEmits<{ open: [id: number] }>();
const { t } = useI18n();
const $q = useQuasar();

const root = ref<HTMLElement | null>(null);
const width = useElementWidth(root);
const perScreen = computed(() => columnsPerScreen(width.value));
// 一次只看得到一欄時沒有地方可拖，只用「移到…」（底部對話框）
const phone = computed(() => perScreen.value <= 1);
const start = ref(0);
const columns = computed(() => props.board.columns);
const groupBy = computed(() => groupByOf(columns.value));
const range = computed(() => visibleRange(Math.floor(start.value / perScreen.value), perScreen.value, columns.value.length));
const shown = computed(() => columns.value.slice(range.value.start, range.value.end));

function columnLabel(c: BoardColumn): string {
  if (c.kind === 'status') return t(`tasks.status.${c.key}`);
  if (c.kind === 'dueBucket') return t(`taskBoard.due.${c.key}`);
  if (c.kind === 'unassigned') return t('tasks.unassigned');
  const name = c.name ?? c.userId ?? c.key;
  return c.inactive ? t('tasks.inactiveUser', { name }) : name;
}
const labelOfKey = (key: string) => {
  const c = columns.value.find((x) => x.key === key);
  return c ? columnLabel(c) : key;
};
const pagerColumns = computed(() => columns.value.map((c) => ({ key: c.key, label: columnLabel(c), total: c.total })));

const loadMore = useLoadMoreColumn(() => props.source);
const loadingKeys = ref<string[]>([]);
async function onLoadMore(column: BoardColumn) {
  loadingKeys.value = [...loadingKeys.value, column.key];
  try {
    await loadMore.mutateAsync(column);
  } catch {
    // 失敗時按鈕恢復可按；錯誤提示由下面的 loadMoreFailed 顯示
  } finally {
    loadingKeys.value = loadingKeys.value.filter((k) => k !== column.key);
  }
}

// ---- 移動（拖拉與「移到…」） ----
const move = useMoveTask(() => props.source);
const movedOut = useMovedOutNotice();
const liveText = ref('');
const findCard = (id: number) => columns.value.flatMap((c) => c.cards).find((c) => c.id === id);

function failureMessage(e: unknown): string {
  const err = e as ApiError;
  if (err.status === 409 && (err.code === 'stale' || !err.message)) return t('taskBoard.stale');
  return Object.values(err.errors ?? {})[0]?.[0] || err.message || t('taskBoard.moveFailed');
}

async function doMove(card: BoardCard, to: BoardColumn, dueDate?: string) {
  try {
    const result = await move.mutateAsync({ card, groupBy: groupBy.value, toColumn: to.key, dueDate });
    if (!result.visible) movedOut.show(card.id, result.undoToken);
    liveText.value = result.visible ? t('taskBoard.moved', { column: columnLabel(to) }) : t('tasks.movedOut');
  } catch (e) {
    const message = failureMessage(e);
    liveText.value = message;
    $q.notify({ type: 'negative', message, position: 'bottom' });
  }
}

// 確認框與日期選擇框的資料另外留著，不跟著開關清掉：按「確定」時 v-close-popup 與 confirm 事件誰先跑都不影響
const confirmOpen = ref(false);
const confirmFor = ref<{ card: BoardCard; to: BoardColumn; flow: MoveFlow } | null>(null);
const dueOpen = ref(false);
const dueFor = ref<{ card: BoardCard; to: BoardColumn } | null>(null);
const confirmTitle = computed(() => (confirmFor.value?.flow === 'confirmCancel' ? t('taskBoard.confirm.cancelTitle') : t('taskBoard.confirm.clearDueTitle')));
const confirmBody = computed(() => (confirmFor.value?.flow === 'confirmCancel' ? t('taskBoard.confirm.cancelBody') : t('taskBoard.confirm.clearDueBody')));

function requestMove(card: BoardCard, columnKey: string) {
  const to = columns.value.find((c) => c.key === columnKey);
  const target = moveTargets(card, columns.value, groupBy.value).find((x) => x.key === columnKey);
  if (!to || !target || target.current) return;
  if (!target.droppable) {
    liveText.value = target.reason ? t(moveBlockKey(target.reason)) : t('taskBoard.drop.unavailable');
    return;
  }
  const flow = moveFlowOf(card, to, groupBy.value);
  if (flow === 'direct') return void doMove(card, to);
  if (flow === 'pickDate') {
    dueFor.value = { card, to };
    dueOpen.value = true;
    return;
  }
  confirmFor.value = { card, to, flow };
  confirmOpen.value = true;
}
function onConfirm() {
  const f = confirmFor.value;
  if (f) void doMove(f.card, f.to);
}
function onDuePicked(date: string) {
  const f = dueFor.value;
  if (f) void doMove(f.card, f.to, date);
}

const drag = useCardDrag({
  enabled: () => !phone.value,
  onDrop: (cardId, key) => {
    const card = findCard(cardId);
    if (card) requestMove(card, key);
  },
});
const draggedCard = computed(() => (drag.state.value ? findCard(drag.state.value.cardId) : undefined));
const dropByKey = computed(() => {
  const card = draggedCard.value;
  const over = drag.state.value?.overKey ?? null;
  const map = new Map<string, ColumnDrop>();
  if (!card) return map;
  for (const x of moveTargets(card, columns.value, groupBy.value)) {
    map.set(x.key, { allowed: x.droppable || x.current, over: over === x.key, reason: x.reason ? t(moveBlockKey(x.reason)) : null });
  }
  return map;
});
</script>

<template>
  <div ref="root" class="task-board" role="region" :aria-label="t('taskBoard.region')" data-testid="task-board">
    <BoardColumnPager v-model:start="start" :columns="pagerColumns" :per-screen="perScreen" />
    <div v-if="board.omittedAssignees > 0" class="text-caption text-muted q-mb-sm" data-testid="board-omitted">
      {{ t('taskBoard.omittedAssignees', { n: board.omittedAssignees }) }}
    </div>
    <div v-if="loadMore.isError.value" class="text-negative text-caption q-mb-sm" role="alert" data-testid="board-load-more-failed">
      {{ t('taskBoard.loadMoreFailed') }}
    </div>
    <div v-if="!columns.length" class="text-muted q-py-md" data-testid="board-empty">{{ t('tasks.empty') }}</div>
    <div v-else class="board-grid" :style="{ gridTemplateColumns: `repeat(${gridTrackCount(columns.length, perScreen)}, minmax(0, 1fr))`, gap: `${BOARD_COLUMN_GAP}px` }" data-testid="board-grid">
      <TaskBoardColumn
        v-for="c in shown" :key="c.key" :column="c" :label="columnLabel(c)" :group-by="groupBy" :loading-more="loadingKeys.includes(c.key)"
        :columns="columns" :label-of="labelOfKey" :phone="phone" :drop="dropByKey.get(c.key) ?? null" :dragging-card-id="drag.state.value?.cardId ?? null"
        @open="emit('open', $event)" @load-more="onLoadMore" @move="requestMove" @drag-start="(e, card) => drag.start(e, card.id)"
      />
    </div>
    <div v-if="draggedCard && drag.state.value" class="board-ghost rounded-borders q-pa-sm text-body2" :style="{ left: `${drag.state.value.x + 12}px`, top: `${drag.state.value.y + 12}px` }" aria-hidden="true" data-testid="board-ghost">
      {{ draggedCard.title }}
    </div>
    <div class="board-live" role="status" aria-live="polite" aria-atomic="true" data-testid="board-live">{{ liveText }}</div>
    <MoveConfirmDialog v-model="confirmOpen" :title="confirmTitle" :body="confirmBody" @confirm="onConfirm" />
    <DuePickDialog v-model="dueOpen" :week-end="board.weekEnd" :default-date="board.laterDefault" @confirm="onDuePicked" />
  </div>
</template>

<style scoped>
.task-board { min-width: 0; max-width: 100%; }
.board-grid { display: grid; align-items: start; }
.board-ghost {
  position: fixed;
  z-index: 6000;
  max-width: 240px;
  overflow: hidden;
  border: 1px solid var(--t-primary);
  background: var(--t-panel);
  white-space: nowrap;
  text-overflow: ellipsis;
  pointer-events: none;
}
/* 只給報讀器：畫面上不佔位 */
.board-live { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
</style>
