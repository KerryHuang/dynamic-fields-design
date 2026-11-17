<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { cardMoveBlock, type BoardCard, type BoardColumn, type BoardGroupBy } from 'src/domain/dynamic/task-board-types';
import { taskStatusColor } from 'src/domain/dynamic/task-types';
import MoveToMenu from './MoveToMenu.vue';

/**
 * 看板卡片（設計 §5.9）。依負責人分欄時省略負責人，依狀態分欄時省略狀態；點卡片（標題上的原生按鈕）開任務詳細。
 * 能移動的卡片（沒凍結、有權限）右上角有「移到…」，按下滑鼠或長按觸控可拖；不能動的卡片游標是 not-allowed、沒有「移到…」。
 */
const props = defineProps<{ card: BoardCard; groupBy: BoardGroupBy; columns: readonly BoardColumn[]; labelOf: (key: string) => string; phone: boolean; dragging?: boolean }>();
const emit = defineEmits<{ open: [id: number]; move: [card: BoardCard, columnKey: string]; dragStart: [event: PointerEvent, card: BoardCard] }>();
const { t } = useI18n();

const assigneeLabel = computed(() => {
  const c = props.card;
  if (!c.assigneeUserId) return t('tasks.unassigned');
  const name = c.assigneeName ?? c.assigneeUserId;
  return c.assigneeActive ? name : t('tasks.inactiveUser', { name });
});
const recordLabel = computed(() => {
  const r = props.card.record;
  if (!r) return '';
  if (!r.accessible) return t('tasks.recordNoAccess');
  if (r.deleted) return t('tasks.recordDeleted', { title: r.title ?? '' });
  return r.title || t('tasks.recordFallback', { id: r.recordId });
});
const movable = computed(() => cardMoveBlock(props.card, props.groupBy) === null);
function onPointerDown(e: PointerEvent) {
  // 「移到…」按鈕自己處理，不要被當成拖拉的起點
  if (!movable.value || (e.target as Element | null)?.closest('.board-card-move')) return;
  emit('dragStart', e, props.card);
}
const closed = computed(() => props.card.status === 'done' || props.card.status === 'cancelled');
const dueClass = computed(() => (props.card.overdue ? 'text-negative' : props.card.dueToday ? 'text-warning' : ''));
</script>

<template>
  <div
    class="board-card q-pa-sm rounded-borders" :class="{ 'is-locked': !movable, 'is-dragging': dragging }" :data-testid="`board-card-${card.id}`"
    @pointerdown="onPointerDown"
  >
    <MoveToMenu v-if="movable" :card="card" :columns="columns" :group-by="groupBy" :label-of="labelOf" :phone="phone" @move="emit('move', card, $event)" />
    <div class="text-body2" :class="{ 'board-card-head': movable }">
      <!-- 原生按鈕負責開啟；::after 撐滿整張卡，滑鼠點卡片任何地方都開。之後「移到…」是同層的另一個按鈕，不會巢狀在按鈕裡 -->
      <button type="button" class="board-card-open" :data-testid="`board-card-open-${card.id}`" @click="emit('open', card.id)">
        <span class="board-card-title" :class="{ 'text-strike text-muted': closed }" data-testid="board-card-title">{{ card.title }}</span>
      </button>
    </div>
    <div v-if="recordLabel" class="text-caption text-muted ellipsis" data-testid="board-card-record">{{ recordLabel }}</div>
    <div class="row items-center q-gutter-x-sm q-mt-xs text-caption">
      <span v-if="card.dueDate" :class="dueClass" data-testid="board-card-due">
        <q-icon v-if="card.overdue || card.dueToday" name="schedule" size="14px" class="q-mr-xs" />{{ card.dueDate }}
      </span>
      <q-badge v-if="groupBy !== 'status'" outline :color="taskStatusColor(card.status)" :label="t(`tasks.status.${card.status}`)" data-testid="board-card-status" />
      <span v-if="groupBy !== 'assignee'" class="row items-center no-wrap" style="min-width: 0; max-width: 100%" data-testid="board-card-assignee">
        <q-icon name="person" size="14px" class="q-mr-xs" /><span class="ellipsis" style="min-width: 0">{{ assigneeLabel }}</span>
      </span>
      <q-space />
      <!-- QIcon 自己是 aria-hidden，名稱要放在外層 span，圖示維持裝飾用 -->
      <span v-if="card.fromRule" class="row items-center" role="img" :aria-label="t('taskBoard.card.fromRule')" data-testid="board-card-rule"><q-icon name="rule" size="16px" /></span>
      <span v-if="card.frozen" class="row items-center" role="img" :aria-label="t('taskBoard.card.frozen')" data-testid="board-card-frozen"><q-icon name="lock" size="16px" /></span>
      <span v-if="card.commentCount > 0" class="row items-center no-wrap" role="img" :aria-label="t('taskBoard.card.comments', { n: card.commentCount })" data-testid="board-card-comments">
        <q-icon name="chat_bubble_outline" size="14px" class="q-mr-xs" />{{ card.commentCount }}
      </span>
    </div>
  </div>
</template>

<style scoped>
.board-card {
  position: relative;
  user-select: none;
  -webkit-touch-callout: none;
  border: 1px solid var(--t-border);
  background: var(--t-panel);
}
.board-card:has(.board-card-open:focus-visible) { outline: 2px solid var(--t-primary); outline-offset: 1px; }
.board-card-open {
  display: block;
  width: 100%;
  padding: 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}
.board-card.is-locked, .board-card.is-locked .board-card-open { cursor: not-allowed; }
.board-card.is-dragging { opacity: 0.45; }
.board-card-head { padding-right: 28px; }
.board-card-open:focus-visible { outline: none; }
.board-card-open::after { content: ''; position: absolute; inset: 0; }
.board-card-title {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}
</style>
