<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { visibleRange } from 'src/domain/dynamic/task-board-types';

/**
 * 欄放不下時的分段平移（D1）。一屏放得下全部欄就不顯示；一屏只有一欄（手機）改成可點的欄位晶片，
 * 其餘是「上一組／下一組」與「第 1–4 欄，共 9 欄」。晶片會換行，不橫向捲動。
 */
const props = defineProps<{
  columns: { key: string; label: string; total: number }[];
  /** 目前第一個可見欄的索引。 */
  start: number;
  perScreen: number;
}>();
const emit = defineEmits<{ 'update:start': [value: number] }>();
const { t } = useI18n();

const range = computed(() => visibleRange(Math.floor(props.start / props.perScreen), props.perScreen, props.columns.length));
const needed = computed(() => props.columns.length > props.perScreen);
const chips = computed(() => props.perScreen === 1);
const text = computed(() => {
  const { start, end } = range.value;
  const total = props.columns.length;
  return end - start <= 1 ? t('taskBoard.pager.rangeOne', { from: start + 1, total }) : t('taskBoard.pager.range', { from: start + 1, to: end, total });
});
const goto = (page: number) => emit('update:start', visibleRange(page, props.perScreen, props.columns.length).start);
</script>

<template>
  <div v-if="needed" class="row items-center no-wrap q-mb-sm" data-testid="board-pager">
    <div v-if="chips" class="row q-gutter-xs" role="group" :aria-label="t('taskBoard.pager.chips')" data-testid="board-chips">
      <q-chip
        v-for="(c, i) in columns" :key="c.key" clickable dense :outline="i !== range.start" color="primary" :text-color="i === range.start ? 'white' : 'primary'"
        :aria-pressed="i === range.start" :title="`${c.label} (${c.total})`" style="max-width: 100%" :data-testid="`board-chip-${c.key}`" @click="emit('update:start', i)"
      ><span class="ellipsis" style="min-width: 0">{{ c.label }} ({{ c.total }})</span></q-chip>
    </div>
    <template v-else>
      <q-btn
        flat dense no-caps icon="chevron_left" :label="t('taskBoard.pager.prev')" :disable="range.page <= 0" data-testid="board-prev"
        @click="goto(range.page - 1)"
      />
      <q-space />
      <div class="text-caption text-muted" aria-live="polite" data-testid="board-range">{{ text }}</div>
      <q-space />
      <q-btn
        flat dense no-caps icon-right="chevron_right" :label="t('taskBoard.pager.next')" :disable="range.page >= range.pageCount - 1"
        data-testid="board-next" @click="goto(range.page + 1)"
      />
    </template>
  </div>
</template>
