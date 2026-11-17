<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { BoardCard, BoardGroupBy } from 'src/domain/dynamic/task-board-types';
import { taskStatusColor } from 'src/domain/dynamic/task-types';

/** 看板卡片（設計 §5.9）。依負責人分欄時省略負責人，依狀態分欄時省略狀態；點卡片（標題上的原生按鈕）開任務詳細。 */
const props = defineProps<{ card: BoardCard; groupBy: BoardGroupBy }>();
const emit = defineEmits<{ open: [id: number] }>();
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
const closed = computed(() => props.card.status === 'done' || props.card.status === 'cancelled');
const dueClass = computed(() => (props.card.overdue ? 'text-negative' : props.card.dueToday ? 'text-warning' : ''));
</script>

<template>
  <div class="board-card q-pa-sm rounded-borders" :data-testid="`board-card-${card.id}`">
    <div class="text-body2">
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
