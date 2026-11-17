<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { BoardCard, BoardColumn, BoardGroupBy, ColumnDrop } from 'src/domain/dynamic/task-board-types';
import TaskCard from './TaskCard.vue';

const props = defineProps<{
  column: BoardColumn; label: string; groupBy: BoardGroupBy; loadingMore: boolean;
  columns: readonly BoardColumn[]; labelOf: (key: string) => string; phone: boolean; drop: ColumnDrop | null; over: boolean; draggingCardId: number | null;
}>();
const emit = defineEmits<{
  open: [id: number]; loadMore: [column: BoardColumn]; move: [card: BoardCard, columnKey: string]; dragStart: [event: PointerEvent, card: BoardCard];
}>();
const { t } = useI18n();

/** 到達畫面上限（500 張）後還沒顯示的筆數；還能載入下一頁時不顯示，先載入。 */
const hiddenCount = computed(() => Math.max(0, props.column.total - props.column.cards.length));
const showTruncated = computed(() => props.column.truncated && !props.column.hasMore && hiddenCount.value > 0);
</script>

<template>
  <div
    class="board-column column no-wrap rounded-borders" :class="{ 'drop-ok': drop?.allowed, 'drop-blocked': drop && !drop.allowed, 'drop-over': drop && over }"
    role="group" tabindex="-1" :data-board-column="column.key" :data-testid="`board-column-${column.key}`" :aria-label="label">
    <header class="row items-center no-wrap q-pa-sm">
      <div class="text-subtitle2 ellipsis col" data-testid="board-column-label">{{ label }}</div>
      <q-badge color="primary" rounded role="img" :label="column.total" :aria-label="t('taskBoard.column.total', { n: column.total })" data-testid="board-column-total" />
    </header>
    <!-- 拖拉中不能放的欄寫出原因；螢幕報讀器的播報由看板的 aria-live 負責，這裡只是畫面提示 -->
    <div v-if="drop && !drop.allowed && drop.reason" class="drop-reason text-caption q-px-sm q-pb-xs" aria-hidden="true" data-testid="board-column-drop-reason">{{ drop.reason }}</div>
    <div class="board-cards q-px-sm q-pb-sm">
      <div v-if="!column.cards.length" class="text-caption text-muted q-pa-sm" data-testid="board-column-empty">{{ t('taskBoard.emptyColumn') }}</div>
      <TaskCard
        v-for="c in column.cards" :key="c.id" :card="c" :group-by="groupBy" :columns="columns" :label-of="labelOf" :phone="phone" :dragging="draggingCardId === c.id"
        @open="emit('open', $event)" @move="(card, key) => emit('move', card, key)" @drag-start="(e, card) => emit('dragStart', e, card)"
      />
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
.board-column.drop-ok { border-style: dashed; border-color: var(--t-primary); }
.board-column.drop-ok.drop-over { background: var(--t-info-soft); border-style: solid; }
.board-column.drop-blocked { background: var(--t-negative-soft); border-style: dashed; border-color: var(--q-negative); cursor: not-allowed; }
.drop-reason { color: var(--q-negative); }
.board-cards { display: flex; flex-direction: column; flex-wrap: nowrap; gap: 8px; }
</style>
