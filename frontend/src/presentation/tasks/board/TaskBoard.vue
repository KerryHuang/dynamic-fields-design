<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useLoadMoreColumn, useMoveTask } from 'src/application/dynamic/queries/task-board-queries';
import {
  BOARD_COLUMN_GAP, columnsPerScreen, gridTrackCount, groupByOf, moveBlockKey, moveFlowOf, moveTargets, visibleRange,
  type BoardCard, type BoardColumn, type BoardGroupBy, type BoardSource, type ColumnDrop, type MoveFlow, type TaskBoardData,
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
const props = defineProps<{ board: TaskBoardData; source: BoardSource; /** 看板設定的分欄依據；還沒讀到設定時不給，由欄的種類反推。 */ groupBy?: BoardGroupBy | null }>();
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
const groupBy = computed(() => groupByOf(columns.value, props.groupBy));
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
// 請求還沒回來的卡片：同一張卡在上一次移動完成前不能再移（版本已經換了，再送一定 409）
const moving = new Set<number>();

/** 只念、不跳通知的結果（成功、拖到不能放的欄）才走這裡；先清空再寫，連續兩次相同結果第二次也會被念出來。有通知的路徑由通知（role=alert）負責念，不要再寫這裡。 */
async function announce(text: string) {
  liveText.value = '';
  await nextTick();
  liveText.value = text;
}

/** 失敗訊息一律由介面語言決定，不顯示伺服器的中文字串；失敗後看板會重抓，禁止狀態很快就會正確，所以只說通用原因。 */
function failureMessage(e: unknown): string {
  const err = e as ApiError;
  if (err.status === 409) return err.code === 'stale' ? t('taskBoard.stale') : t('taskBoard.drop.frozen');
  if (err.status === 404) return t('tasks.notFound');
  if (err.status === 400 && err.errors?.dueDate) return t('taskBoard.duePick.invalid', { date: props.board.weekEnd });
  if (err.status === 400 && err.errors?.toColumn) return t('taskBoard.drop.unavailable');
  return t('taskBoard.moveFailed');
}

// ---- 「移到…」之後的鍵盤焦點 ----
// 樂觀更新會讓原本那張卡換欄（重新掛載），焦點所在的按鈕跟著消失；記住原欄的鄰居，之後把焦點放回去（WCAG 2.4.3）。
interface FocusPlan { cardId: number; fromKey: string; nextId: number | null; prevId: number | null }
function focusPlanOf(card: BoardCard): FocusPlan | null {
  const col = columns.value.find((c) => c.cards.some((x) => x.id === card.id));
  if (!col) return null;
  const i = col.cards.findIndex((x) => x.id === card.id);
  return { cardId: card.id, fromKey: col.key, nextId: col.cards[i + 1]?.id ?? null, prevId: col.cards[i - 1]?.id ?? null };
}
/**
 * 焦點掉到 body、或還停在正在關閉的選單裡（卡片被換掉了）才處理；使用者已經去別處就不搶。
 * 先等一個 macrotask，樂觀更新（onMutate 是非同步的）與重新 render 都完成後再找。順序：這張卡的「移到…」→ 原欄下一張 → 上一張 → 原欄本身。
 */
async function restoreFocus(plan: FocusPlan) {
  await new Promise((r) => setTimeout(r));
  await nextTick();
  applyFocus(plan);
}
/** `inMenu`：焦點還停在正在關閉的選單裡也算沒人接手；追蹤階段（移動已結束）不算，那是使用者在開下一張卡的選單。 */
function applyFocus(plan: FocusPlan, inMenu = true): boolean {
  const active = document.activeElement;
  if (active && active !== document.body && !(inMenu && active.closest('[data-testid="move-list"]'))) return false;
  const el = root.value;
  if (!el) return false;
  const moveButton = (id: number | null) => (id === null ? null : el.querySelector<HTMLElement>(`[data-testid="board-card-move-${id}"]`));
  const column = [...el.querySelectorAll<HTMLElement>('[data-board-column]')].find((c) => c.getAttribute('data-board-column') === plan.fromKey);
  (moveButton(plan.cardId) ?? moveButton(plan.nextId) ?? moveButton(plan.prevId) ?? column)?.focus();
  return true;
}
/**
 * 移動結束後的重抓結果可能讓卡片換位置（例如伺服器把它排在目標欄後面）；瀏覽器搬動有焦點的節點會讓焦點掉到 body。
 * 重抓比 restoreFocus 的等待慢，所以移動結束後短時間內，看板資料一變就再放一次；使用者已去別處就停。
 */
let followPlan: FocusPlan | null = null;
let followTimer: ReturnType<typeof setTimeout> | undefined;
function followFocus(plan: FocusPlan) {
  followPlan = plan;
  clearTimeout(followTimer);
  followTimer = setTimeout(() => { followPlan = null; }, 5000);
}
watch(() => props.board, async () => {
  const plan = followPlan;
  if (!plan) return;
  await nextTick();
  if (!applyFocus(plan, false)) followPlan = null;
});
onBeforeUnmount(() => clearTimeout(followTimer));

async function doMove(card: BoardCard, to: BoardColumn, dueDate?: string, focus: FocusPlan | null = null) {
  if (moving.has(card.id)) return;
  followPlan = null;
  moving.add(card.id);
  try {
    const run = move.mutateAsync({ card, groupBy: groupBy.value, toColumn: to.key, dueDate });
    if (focus) void restoreFocus(focus);
    const result = await run;
    if (!result.visible) {
      movedOut.show(card.id, result.undoToken);
      liveText.value = '';
    } else void announce(t('taskBoard.moved', { column: columnLabel(to) }));
  } catch (e) {
    liveText.value = '';
    $q.notify({ type: 'negative', message: failureMessage(e), position: 'bottom' });
  } finally {
    moving.delete(card.id);
    if (focus) {
      void restoreFocus(focus);
      followFocus(focus);
    }
  }
}

// 確認框與日期選擇框的資料另外留著，不跟著開關清掉：按「確定」時 v-close-popup 與 confirm 事件誰先跑都不影響
const confirmOpen = ref(false);
const confirmFor = ref<{ card: BoardCard; to: BoardColumn; flow: MoveFlow; focus: FocusPlan | null } | null>(null);
const dueOpen = ref(false);
const dueFor = ref<{ card: BoardCard; to: BoardColumn; focus: FocusPlan | null } | null>(null);
const confirmTitle = computed(() => (confirmFor.value?.flow === 'confirmCancel' ? t('taskBoard.confirm.cancelTitle') : t('taskBoard.confirm.clearDueTitle')));
const confirmBody = computed(() => (confirmFor.value?.flow === 'confirmCancel' ? t('taskBoard.confirm.cancelBody') : t('taskBoard.confirm.clearDueBody')));

function requestMove(card: BoardCard, columnKey: string, via: 'drag' | 'menu' = 'drag') {
  if (moving.has(card.id)) return;
  const to = columns.value.find((c) => c.key === columnKey);
  const target = moveTargets(card, columns.value, groupBy.value).find((x) => x.key === columnKey);
  if (!to || !target || target.current) return;
  if (!target.droppable) {
    void announce(target.reason ? t(moveBlockKey(target.reason)) : t('taskBoard.drop.unavailable'));
    return;
  }
  const focus = via === 'menu' ? focusPlanOf(card) : null;
  const flow = moveFlowOf(card, to, groupBy.value);
  if (flow === 'direct') return void doMove(card, to, undefined, focus);
  if (flow === 'pickDate') {
    dueFor.value = { card, to, focus };
    dueOpen.value = true;
    return;
  }
  confirmFor.value = { card, to, flow, focus };
  confirmOpen.value = true;
}
function onConfirm() {
  const f = confirmFor.value;
  if (f) void doMove(f.card, f.to, undefined, f.focus);
}
function onDuePicked(date: string) {
  const f = dueFor.value;
  if (f) void doMove(f.card, f.to, date, f.focus);
}

const drag = useCardDrag({
  enabled: () => !phone.value,
  touchRoot: root,
  onDrop: (cardId, key) => {
    const card = findCard(cardId);
    if (card) requestMove(card, key);
  },
});
const draggedCard = computed(() => (drag.state.value ? findCard(drag.state.value.cardId) : undefined));
// 只依賴被拖的卡與欄，指標移動時不重算；指標在哪一欄另外用 over 傳，這樣每次移動只有前後兩欄要重新 render
const dropByKey = computed(() => {
  const card = draggedCard.value;
  const map = new Map<string, ColumnDrop>();
  if (!card) return map;
  for (const x of moveTargets(card, columns.value, groupBy.value)) {
    map.set(x.key, { allowed: x.droppable || x.current, reason: x.reason ? t(moveBlockKey(x.reason)) : null });
  }
  return map;
});
const overKey = computed(() => drag.state.value?.overKey ?? null);
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
        :columns="columns" :label-of="labelOfKey" :phone="phone" :drop="dropByKey.get(c.key) ?? null" :over="overKey === c.key" :dragging-card-id="drag.state.value?.cardId ?? null"
        @open="emit('open', $event)" @load-more="onLoadMore" @move="(card, key) => requestMove(card, key, 'menu')" @drag-start="(e, card) => { if (!moving.has(card.id)) drag.start(e, card.id); }"
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
