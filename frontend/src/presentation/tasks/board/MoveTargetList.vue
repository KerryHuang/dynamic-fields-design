<script setup lang="ts">
import { nextTick, onMounted, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { moveBlockKey, type MoveTarget } from 'src/domain/dynamic/task-board-types';

/** 「移到…」選單／對話框共用的欄清單（鍵盤：方向鍵與 Home／End 移動焦點，Enter 或空白鍵選取）。不能放的欄灰掉但仍可聚焦，原因寫在項目裡讓報讀器讀得到。 */
const props = defineProps<{ targets: MoveTarget[]; labelOf: (key: string) => string }>();
const emit = defineEmits<{ pick: [key: string] }>();
const { t } = useI18n();

const list = ref<HTMLElement | null>(null);
// 目前所在的欄也不能選（移到原地沒有意義），但仍可聚焦、讀得到「目前所在」
const unavailable = (x: MoveTarget) => !x.droppable || x.current;
const firstEnabled = () => props.targets.findIndex((x) => !unavailable(x));
// 選單／對話框開啟後焦點進到第一個能選的欄（沒有就第一項），方向鍵才有地方起步
onMounted(() => nextTick(() => {
  const items = list.value?.querySelectorAll<HTMLElement>('[role="menuitem"]');
  items?.[Math.max(0, firstEnabled())]?.focus();
}));

function onKey(e: KeyboardEvent) {
  const items = [...(list.value?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
  if (!items.length) return;
  const at = items.indexOf(document.activeElement as HTMLElement);
  const to = e.key === 'ArrowDown' ? (at + 1) % items.length
    : e.key === 'ArrowUp' ? (at <= 0 ? items.length - 1 : at - 1)
      : e.key === 'Home' ? 0 : e.key === 'End' ? items.length - 1 : -1;
  if (to < 0) return;
  e.preventDefault();
  items[to].focus();
}
function pick(x: MoveTarget) {
  if (!unavailable(x)) emit('pick', x.key);
}
</script>

<template>
  <div ref="list" role="menu" class="move-list column no-wrap" :aria-label="t('taskBoard.moveTo.title')" data-testid="move-list" @keydown="onKey">
    <button
      v-for="(x, i) in targets" :key="x.key" type="button" role="menuitem" class="move-item text-left" :class="{ 'is-disabled': unavailable(x) }"
      :aria-disabled="unavailable(x)" :aria-current="x.current ? 'true' : undefined" :data-autofocus="i === firstEnabled() ? '' : undefined"
      :data-testid="`move-item-${x.key}`" @click="pick(x)"
    >
      <span class="move-label">{{ labelOf(x.key) }}</span>
      <span v-if="x.current" class="move-note text-caption text-muted">{{ t('taskBoard.moveTo.current') }}</span>
      <span v-else-if="x.reason" class="move-note text-caption text-muted" data-testid="move-reason">{{ t(moveBlockKey(x.reason)) }}</span>
    </button>
  </div>
</template>

<style scoped>
.move-list { min-width: 220px; padding: 4px 0; }
.move-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-height: 40px;
  justify-content: center;
  padding: 6px 16px;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  cursor: pointer;
}
.move-item:hover:not(.is-disabled), .move-item:focus-visible { background: var(--t-info-soft); outline: none; }
.move-item:focus-visible { box-shadow: inset 0 0 0 2px var(--t-primary); }
.move-item.is-disabled { cursor: not-allowed; opacity: 0.7; }
.move-label { overflow-wrap: anywhere; }
</style>
