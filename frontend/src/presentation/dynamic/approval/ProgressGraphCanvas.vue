<script setup lang="ts">
import '@vue-flow/core/dist/style.css';
import { BaseEdge, VueFlow, getSmoothStepPath, useVueFlow, type Edge, type EdgeProps, type Node } from '@vue-flow/core';
import { computed, h, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import ProgressGraphNode from './ProgressGraphNode.vue';
import { MAX_ZOOM, viewportTransform, type GraphMode, type ProgressEdgeData, type ProgressNodeData, type ViewportPlan } from './progress-graph';

// 唯讀的進度圖畫布（fg4 計畫 J12）。Vue Flow 只在這個檔案與 ProgressGraphNode.vue 出現，由 ApprovalProgressGraph 動態載入。
const props = defineProps<{
  instanceId: number; mode: GraphMode; nodes: Node<ProgressNodeData>[]; edges: Edge[];
  plan: ViewportPlan; width: number; height: number;
}>();
const emit = defineEmits<{ 'node-open': [nodeId: string] }>();
const { t } = useI18n();
const id = computed(() => `progress-${props.instanceId}-${props.mode}`);
// useVueFlow 與 <VueFlow> 用同一個 id 共用 store；每個畫布各自一份，主實例、先前的簽核、縮圖與全螢幕不會互相影響。
const flow = useVueFlow(id.value);
const transform = computed(() => viewportTransform(props.plan, props.width, props.height));
const interactive = computed(() => props.mode !== 'thumb');

function apply() { void flow.setViewport(transform.value); }
// 寬度、高度或圖本身改變（轉向、換語系）就重新對準。分開監看三個數字：值沒變（例如只是 plan 重算）就不重置使用者拖曳過的視角。
watch([() => transform.value.x, () => transform.value.y, () => transform.value.zoom], apply);

// 線：內建 smoothstep，但轉折點由 progress-graph 算好（data.bendX，永遠落在欄距），線才不會切過別條泳道的節點。
// 標籤被截斷時，完整文字放進 SVG <title>（滑鼠移上去可看）。
function ProgressEdge(p: EdgeProps<ProgressEdgeData>) {
  const [path, labelX, labelY] = getSmoothStepPath({
    sourceX: p.sourceX, sourceY: p.sourceY, sourcePosition: p.sourcePosition,
    targetX: p.targetX, targetY: p.targetY, targetPosition: p.targetPosition, centerX: p.data?.bendX,
  });
  const short = typeof p.label === 'string' ? p.label : '';
  const full = p.data?.fullLabel ?? short;
  const label = short && full !== short ? () => [h('title', full), short] : short;
  return h(BaseEdge, { id: p.id, path, labelX, labelY, label: label || undefined, interactionWidth: 0, labelBgPadding: [4, 2] });
}
const fit = () => { void flow.fitView({ padding: 0.08, duration: 200 }); };
const zoomIn = () => { void flow.zoomIn({ duration: 150 }); };
const zoomOut = () => { void flow.zoomOut({ duration: 150 }); };
</script>

<template>
  <!-- 縮圖不能操作，也不讓讀屏與鍵盤進去：整個畫布 inert、aria-hidden，由外層的「放大檢視」按鈕代表它。 -->
  <div
    class="pg-canvas" :class="{ 'pg-thumb': !interactive }" role="group" :aria-label="t('approval.graph.label')" :aria-hidden="interactive ? undefined : 'true'"
    :inert="interactive ? undefined : true" data-testid="pg-canvas"
  >
    <VueFlow
      :id="id" :nodes="nodes" :edges="edges" :default-viewport="transform" :min-zoom="plan.minZoom" :max-zoom="MAX_ZOOM"
      :nodes-draggable="false" :nodes-connectable="false" :elements-selectable="false" :nodes-focusable="false" :edges-focusable="false"
      :disable-keyboard-a11y="true" :zoom-on-scroll="false" :pan-on-scroll="false" :zoom-on-double-click="false" :prevent-scrolling="false"
      :zoom-on-pinch="interactive" :pan-on-drag="interactive" :fit-view-on-init="false" :delete-key-code="null" :selection-key-code="null"
      :multi-selection-key-code="null"
      @pane-ready="apply"
    >
      <template #node-progress="p"><ProgressGraphNode :data="p.data" :interactive="interactive" @open="emit('node-open', p.id)" /></template>
      <template #node-addSign="p"><ProgressGraphNode :data="p.data" :interactive="interactive" @open="emit('node-open', p.id)" /></template>
      <template #edge-progress="p"><ProgressEdge v-bind="p" /></template>
    </VueFlow>
    <div v-if="interactive" class="pg-toolbar">
      <q-btn dense flat round size="sm" icon="add" :aria-label="t('approval.graph.zoomIn')" :title="t('approval.graph.zoomIn')" data-testid="pg-zoom-in" @click="zoomIn" />
      <q-btn dense flat round size="sm" icon="remove" :aria-label="t('approval.graph.zoomOut')" :title="t('approval.graph.zoomOut')" data-testid="pg-zoom-out" @click="zoomOut" />
      <q-btn dense flat round size="sm" icon="fit_screen" :aria-label="t('approval.graph.fit')" :title="t('approval.graph.fit')" data-testid="pg-fit" @click="fit" />
    </div>
  </div>
</template>

<style>
/* 不載入 Vue Flow 的 theme-default.css；線、文字與底色都用主題 token，深色主題一併生效。 */
.pg-canvas { position: relative; width: 100%; height: 100%; }
.pg-canvas.pg-thumb { pointer-events: none; }
.pg-canvas .vue-flow { background: transparent; }
.pg-canvas .vue-flow__pane.draggable { cursor: grab; }
.pg-canvas .vue-flow__edge { cursor: inherit; }
.pg-canvas .vue-flow__node { cursor: inherit; }
.pg-canvas .vue-flow__edge-path { stroke: var(--t-muted); fill: none; }
.pg-canvas .pg-edge-on .vue-flow__edge-path { stroke: var(--t-primary); stroke-width: 2.5; }
.pg-canvas .pg-edge-off .vue-flow__edge-path { stroke: var(--t-muted); stroke-width: 1; stroke-dasharray: 4 4; }
.pg-canvas .vue-flow__edge-text { fill: var(--t-text); font-size: 11px; }
.pg-canvas .vue-flow__edge-textbg { fill: var(--t-panel); }
.pg-canvas .pg-toolbar {
  position: absolute; top: 4px; right: 4px; z-index: 5; display: flex; gap: 2px; padding: 2px;
  background: var(--t-panel); border: 1px solid var(--t-border); border-radius: var(--t-radius);
}
</style>
