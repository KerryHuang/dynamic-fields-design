<script setup lang="ts">
import { GridItem, GridLayout } from 'grid-layout-plus';
import { ref, watch } from 'vue';
import { GRID_COLUMNS, readingOrder } from 'src/domain/dynamic/dashboard-layout';
import type { DashboardCard } from 'src/domain/dynamic/dashboard-types';

/**
 * 卡片版面：寬螢幕是 12 欄的格子（編輯時可拖放與調整大小），窄螢幕（小於 600px）單欄由上到下排列、不可拖放。
 * 只在使用者放開拖放或縮放時送出 change，初始化與外部更新不會觸發儲存。
 */
const props = defineProps<{ cards: DashboardCard[]; editable: boolean; narrow: boolean }>();
const emit = defineEmits<{ change: [positions: { i: string; x: number; y: number; w: number; h: number }[]] }>();

interface Item { i: string; x: number; y: number; w: number; h: number }
const items = ref<Item[]>([]);
watch(() => props.cards, (cards) => { items.value = cards.map((c) => ({ i: c.id, x: c.x, y: c.y, w: c.w, h: c.h })); }, { immediate: true, deep: true });

const cardOf = (id: string) => props.cards.find((c) => c.id === id)!;
function done() { emit('change', items.value.map(({ i, x, y, w, h }) => ({ i, x, y, w, h }))); }
</script>

<template>
  <div v-if="narrow" class="column q-gutter-y-md" data-testid="dashboard-narrow">
    <div v-for="c in readingOrder(cards)" :key="c.id" class="dashboard-narrow-card" :data-testid="`grid-card-${c.id}`">
      <slot name="card" :card="c" />
    </div>
  </div>
  <GridLayout
    v-else
    v-model:layout="items" :col-num="GRID_COLUMNS" :row-height="70" :margin="[12, 12]"
    :is-draggable="editable" :is-resizable="editable" vertical-compact use-css-transforms
    drag-allow-from=".card-drag-handle" data-testid="dashboard-grid"
  >
    <GridItem
      v-for="item in items" :key="item.i" :i="item.i" :x="item.x" :y="item.y" :w="item.w" :h="item.h" :min-w="2" :min-h="2"
      :data-testid="`grid-card-${item.i}`" @moved="done" @resized="done"
    >
      <slot name="card" :card="cardOf(item.i)" />
    </GridItem>
  </GridLayout>
</template>

<style scoped>
/* Quasar 的 .column 會換行，換行的 flex 直欄裡項目寬度取最寬內容；限制最大寬度，寬表格才會在卡片內捲動而不是撐寬整頁。 */
.dashboard-narrow-card {
  min-height: 160px;
  max-width: 100%;
}
</style>
