<script setup lang="ts">
import { QSkeleton, useQuasar } from 'quasar';
import { computed, defineAsyncComponent, defineComponent, h, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalProgressGraph, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import { buildProgressGraph, graphHeight, initialViewport, GRAPH_MIN_HEIGHT } from './progress-graph';

// 簽核進度圖（寬螢幕，≥ 600）。外層只算排版、圖高與視角，畫布與 Vue Flow 在 ProgressGraphCanvas 動態載入，
// 所以排版與對照留在詳情頁的塊裡、載入前就知道高度，骨架與畫布一樣高、不會跳動（fg4 計畫 J3）。
const props = defineProps<{ graph: ApprovalProgressGraph; stages: ApprovalStageView[]; instanceId: number }>();
const { t, locale } = useI18n();
const $q = useQuasar();

// 手機（< 600）這段先不畫圖，縮圖在 fg4d。
const wide = computed(() => !$q.screen.lt.sm);

const MODE = 'inline';
const built = computed(() => buildProgressGraph(props.graph, props.stages, { locale: locale.value, t }));
const width = ref(0);
const maxHeight = computed(() => $q.screen.height * 0.6);
const plan = computed(() => (width.value > 0 ? initialViewport(built.value.layout, width.value, MODE, { focus: built.value.focus, maxHeight: Math.max(GRAPH_MIN_HEIGHT, maxHeight.value) }) : null));
const height = computed(() => (width.value > 0 ? graphHeight(built.value.layout, width.value, MODE, $q.screen.height) : GRAPH_MIN_HEIGHT));

// 載入中的骨架不吃外層傳給畫布的屬性（width、height 是數字，QSkeleton 要字串）。
const Skeleton = defineComponent({ inheritAttrs: false, render: () => h(QSkeleton, { square: true, height: '100%', width: '100%' }) });
const Canvas = defineAsyncComponent({ loader: () => import('./ProgressGraphCanvas.vue'), loadingComponent: Skeleton, delay: 0 });

// 寬度取畫布框內側，隨視窗或版面改變重新算視角與高度。
const frame = ref<HTMLElement | null>(null);
watch(frame, (el, _old, onCleanup) => {
  if (!el) return;
  const measure = () => { width.value = el.clientWidth; };
  measure();
  if (typeof ResizeObserver === 'undefined') return;
  const observer = new ResizeObserver(measure);
  observer.observe(el);
  onCleanup(() => observer.disconnect());
}, { flush: 'post' });
</script>

<template>
  <div v-if="wide" class="approval-progress-graph q-mb-md" data-testid="approval-progress-graph">
    <div ref="frame" class="pg-frame" :style="{ height: `${height}px` }">
      <Canvas
        v-if="plan" :instance-id="instanceId" mode="inline" :nodes="built.nodes" :edges="built.edges" :plan="plan" :width="width" :height="height"
      />
      <QSkeleton v-else square height="100%" width="100%" data-testid="pg-skeleton" />
    </div>
  </div>
</template>

<style scoped>
.pg-frame { box-sizing: content-box; border: 1px solid var(--t-border); border-radius: var(--t-radius); background: var(--t-panel); overflow: hidden; }
</style>
