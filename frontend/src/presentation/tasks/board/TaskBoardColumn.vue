<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { BoardColumn, BoardGroupBy } from 'src/domain/dynamic/task-board-types';
import TaskCard from './TaskCard.vue';

const props = defineProps<{ column: BoardColumn; label: string; groupBy: BoardGroupBy; loadingMore: boolean }>();
const emit = defineEmits<{ open: [id: number]; loadMore: [column: BoardColumn] }>();
const { t } = useI18n();

/** 到達畫面上限（500 張）後還沒顯示的筆數；還能載入下一頁時不顯示，先載入。 */
const hiddenCount = computed(() => Math.max(0, props.column.total - props.column.cards.length));
const showTruncated = computed(() => props.column.truncated && !props.column.hasMore && hiddenCount.value > 0);
</script>

<template>
  <div class="board-column column no-wrap rounded-borders" role="group" :data-board-column="column.key" :data-testid="`board-column-${column.key}`" :aria-label="label">
    <header class="row items-center no-wrap q-pa-sm">
      <div class="text-subtitle2 ellipsis col" data-testid="board-column-label">{{ label }}</div>
      <q-badge color="primary" rounded :label="column.total" :aria-label="t('taskBoard.column.total', { n: column.total })" data-testid="board-column-total" />
    </header>
    <div class="board-cards q-px-sm q-pb-sm">
      <div v-if="!column.cards.length" class="text-caption text-muted q-pa-sm" data-testid="board-column-empty">{{ t('taskBoard.emptyColumn') }}</div>
      <TaskCard v-for="c in column.cards" :key="c.id" :card="c" :group-by="groupBy" @open="emit('open', $event)" />
      <q-btn
        v-if="column.hasMore" flat dense no-caps color="primary" :label="t('taskBoard.loadMore')" :loading="loadingMore"
        data-testid="board-load-more" @click="emit('loadMore', column)"
      />
      <div v-if="showTruncated" class="text-caption text-muted" data-testid="board-truncated">{{ t('taskBoard.truncated', { n: hiddenCount }) }}</div>
    </div>
  </div>
</template>

<style scoped>
.board-column { min-width: 0; border: 1px solid var(--t-border); background: var(--t-bg); }
.board-cards { display: flex; flex-direction: column; flex-wrap: nowrap; gap: 8px; }
</style>
