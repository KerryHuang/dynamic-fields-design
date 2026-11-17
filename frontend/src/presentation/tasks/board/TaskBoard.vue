<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useLoadMoreColumn } from 'src/application/dynamic/queries/task-board-queries';
import {
  BOARD_COLUMN_GAP, columnsPerScreen, gridTrackCount, groupByOf, visibleRange, type BoardColumn, type BoardSource, type TaskBoardData,
} from 'src/domain/dynamic/task-board-types';
import BoardColumnPager from './BoardColumnPager.vue';
import TaskBoardColumn from './TaskBoardColumn.vue';
import { useElementWidth } from './use-element-width';

/**
 * 看板：欄依容器寬度分段顯示（D1），不換行也不橫向捲動；全部欄一屏放得下時撐滿寬度，要分段時固定一屏欄數。`source` 只用來把「載入更多」接回同一份快取。
 */
const props = defineProps<{ board: TaskBoardData; source: BoardSource }>();
const emit = defineEmits<{ open: [id: number] }>();
const { t } = useI18n();

const root = ref<HTMLElement | null>(null);
const width = useElementWidth(root);
const perScreen = computed(() => columnsPerScreen(width.value));
const start = ref(0);
const columns = computed(() => props.board.columns);
const groupBy = computed(() => groupByOf(columns.value));
const range = computed(() => visibleRange(Math.floor(start.value / perScreen.value), perScreen.value, columns.value.length));
const shown = computed(() => columns.value.slice(range.value.start, range.value.end));

function labelOf(c: BoardColumn): string {
  if (c.kind === 'status') return t(`tasks.status.${c.key}`);
  if (c.kind === 'dueBucket') return t(`taskBoard.due.${c.key}`);
  if (c.kind === 'unassigned') return t('tasks.unassigned');
  const name = c.name ?? c.userId ?? c.key;
  return c.inactive ? t('tasks.inactiveUser', { name }) : name;
}
const pagerColumns = computed(() => columns.value.map((c) => ({ key: c.key, label: labelOf(c), total: c.total })));

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
        v-for="c in shown" :key="c.key" :column="c" :label="labelOf(c)" :group-by="groupBy" :loading-more="loadingKeys.includes(c.key)"
        @open="emit('open', $event)" @load-more="onLoadMore"
      />
    </div>
  </div>
</template>

<style scoped>
.task-board { min-width: 0; max-width: 100%; }
.board-grid { display: grid; align-items: start; }
</style>
