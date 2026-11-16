<script setup lang="ts">
import '@vue-flow/core/dist/style.css';
import { VueFlow, useVueFlow, type Edge, type Node } from '@vue-flow/core';
import { computed, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import ProgressGraphNode from './ProgressGraphNode.vue';
import { MAX_ZOOM, viewportTransform, type GraphMode, type ProgressNodeData, type ViewportPlan } from './progress-graph';

// 唯讀的進度圖畫布（fg4 計畫 J12）。Vue Flow 只在這個檔案與 ProgressGraphNode.vue 出現，由 ApprovalProgressGraph 動態載入。
const props = defineProps<{
  instanceId: number; mode: GraphMode; nodes: Node<ProgressNodeData>[]; edges: Edge[];
  plan: ViewportPlan; width: number; height: number;
}>();
const { t } = useI18n();
const id = computed(() => `progress-${props.instanceId}-${props.mode}`);
// useVueFlow 與 <VueFlow> 用同一個 id 共用 store；每個畫布各自一份，主實例、先前的簽核、縮圖與全螢幕不會互相影響。
const flow = useVueFlow(id.value);
const transform = computed(() => viewportTransform(props.plan, props.width, props.height));
const interactive = computed(() => props.mode !== 'thumb');

function apply() { void flow.setViewport(transform.value); }
// 寬度、高度或圖本身改變（轉向、換語系）就重新對準；使用者拖曳過的視角會被重置，這是預期的。
watch(() => [transform.value.x, transform.value.y, transform.value.zoom, props.plan.minZoom], apply);
const fit = () => { void flow.fitView({ padding: 0.08, duration: 200 }); };
const zoomIn = () => { void flow.zoomIn({ duration: 150 }); };
const zoomOut = () => { void flow.zoomOut({ duration: 150 }); };
</script>

<template>
  <div class="pg-canvas" role="group" :aria-label="t('approval.graph.label')" data-testid="pg-canvas">
    <VueFlow
      :id="id" :nodes="nodes" :edges="edges" :default-viewport="transform" :min-zoom="plan.minZoom" :max-zoom="MAX_ZOOM"
      :nodes-draggable="false" :nodes-connectable="false" :elements-selectable="false" :nodes-focusable="false" :edges-focusable="false"
      :disable-keyboard-a11y="true" :zoom-on-scroll="false" :pan-on-scroll="false" :zoom-on-double-click="false" :prevent-scrolling="false"
      :zoom-on-pinch="interactive" :pan-on-drag="interactive" :fit-view-on-init="false" :delete-key-code="null" :selection-key-code="null"
      :multi-selection-key-code="null"
      @pane-ready="apply"
    >
      <template #node-progress="p"><ProgressGraphNode :data="p.data" /></template>
      <template #node-addSign="p"><ProgressGraphNode :data="p.data" /></template>
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
