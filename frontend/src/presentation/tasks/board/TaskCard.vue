<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { BoardCard, BoardGroupBy } from 'src/domain/dynamic/task-board-types';
import { taskStatusColor } from 'src/domain/dynamic/task-types';

/** 看板卡片（設計 §5.9）。依負責人分欄時省略負責人，依狀態分欄時省略狀態；點卡片開任務詳細。 */
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
  <div
    class="board-card q-pa-sm rounded-borders" role="button" tabindex="0" :data-testid="`board-card-${card.id}`"
    @click="emit('open', card.id)" @keydown.enter.self="emit('open', card.id)" @keydown.space.self.prevent="emit('open', card.id)"
  >
    <div class="board-card-title text-body2" :class="{ 'text-strike text-muted': closed }" data-testid="board-card-title">{{ card.title }}</div>
    <div v-if="recordLabel" class="text-caption text-muted ellipsis" data-testid="board-card-record">{{ recordLabel }}</div>
    <div class="row items-center q-gutter-x-sm q-mt-xs text-caption">
      <span v-if="card.dueDate" :class="dueClass" data-testid="board-card-due">
        <q-icon v-if="card.overdue || card.dueToday" name="schedule" size="14px" class="q-mr-xs" />{{ card.dueDate }}
      </span>
      <q-badge v-if="groupBy !== 'status'" outline :color="taskStatusColor(card.status)" :label="t(`tasks.status.${card.status}`)" data-testid="board-card-status" />
      <span v-if="groupBy !== 'assignee'" class="row items-center no-wrap" data-testid="board-card-assignee">
        <q-icon name="person" size="14px" class="q-mr-xs" />{{ assigneeLabel }}
      </span>
      <q-space />
      <q-icon v-if="card.fromRule" name="rule" size="16px" :aria-label="t('taskBoard.card.fromRule')" role="img" data-testid="board-card-rule" />
      <q-icon v-if="card.frozen" name="lock" size="16px" :aria-label="t('taskBoard.card.frozen')" role="img" data-testid="board-card-frozen" />
      <span v-if="card.commentCount > 0" class="row items-center no-wrap" role="img" :aria-label="t('taskBoard.card.comments', { n: card.commentCount })" data-testid="board-card-comments">
        <q-icon name="chat_bubble_outline" size="14px" class="q-mr-xs" />{{ card.commentCount }}
      </span>
    </div>
  </div>
</template>

<style scoped>
.board-card {
  border: 1px solid var(--t-border);
  background: var(--t-panel);
  cursor: pointer;
}
.board-card:focus-visible { outline: 2px solid var(--t-primary); outline-offset: 1px; }
.board-card-title {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}
</style>
