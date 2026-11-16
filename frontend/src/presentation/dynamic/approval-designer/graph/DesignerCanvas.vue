<script setup lang="ts">
import '@vue-flow/core/dist/style.css';
import { VueFlow, useVueFlow, type Edge, type Node } from '@vue-flow/core';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { NODE_H, NODE_W } from 'src/domain/dynamic/approval-graph-layout';
import DesignerEdge from './DesignerEdge.vue';
import DesignerNode from './DesignerNode.vue';
import type { DesignerEdgeData, DesignerNodeData } from './designer-graph';

// 設計頁的畫布（J69、J75）。Vue Flow 只在 DesignerCanvas / DesignerNode / DesignerEdge 出現，整個設計頁是路由層動態載入的獨立塊。
// 鍵盤：Vue Flow 內建的方向鍵移動只發 nodesChange、不發 nodeDragStop，不會寫回工作副本，所以整組關掉（disable-keyboard-a11y）；
// 外層 wrapper 不再可聚焦（每個節點只剩 DesignerNode 內層一個 Tab 停點，Enter／Space 開面板），也不再帶「press delete」的描述。鍵盤移動留給 fg3e（要進復原堆疊、套 snap）。
// 平移到某節點、fitView 一律由這裡以 defineExpose 提供（use-graph-designer 不碰 Vue Flow）。
const props = defineProps<{ canvasId: string; nodes: Node<DesignerNodeData>[]; edges: Edge<DesignerEdgeData>[]; draggable: boolean; initialNodes?: string[] }>();
const emit = defineEmits<{ 'node-open': [id: string]; 'edge-open': [id: string]; 'pane-click': []; 'node-move': [id: string, to: { x: number; y: number }] }>();
const { t } = useI18n();
const flow = useVueFlow(props.canvasId);
const GRID = 8;
const FIT_PADDING = 0.12;

// 手機的首屏（J66，使用者 11-16 確認）：整張圖縮進 390px 只剩約 38px 寬的節點，所以改成只框住起點附近的 `initialNodes`（縮放 0.6–1）；
// 節點量好尺寸後（nodesInitialized）才能 fitView，只做第一次。「全圖」鈕、平板以上不受影響。
let fittedInitial = false;
function fitInitial() {
  if (fittedInitial || !props.initialNodes?.length) return;
  fittedInitial = true;
  void flow.fitView({ nodes: props.initialNodes, padding: 0.2, minZoom: 0.6, maxZoom: 1 });
}
const fitAll = () => { void flow.fitView({ padding: FIT_PADDING, duration: 200 }); };
/** 把節點移到畫布可見區中央；`rightInset` 是右側被抽屜擋住的寬度（平板的屬性抽屜）。 */
function focusNode(id: string, rightInset = 0) {
  const n = props.nodes.find((x) => x.id === id);
  if (!n) return;
  const zoom = Math.max(flow.viewport.value.zoom, 0.6);
  void flow.setCenter(n.position.x + NODE_W / 2 + rightInset / 2 / zoom, n.position.y + NODE_H / 2, { zoom, duration: 200 });
}
defineExpose({ fitAll, focusNode });
const zoomIn = () => { void flow.zoomIn({ duration: 150 }); };
const zoomOut = () => { void flow.zoomOut({ duration: 150 }); };
const editable = computed(() => props.draggable);
</script>

<template>
  <div class="de-canvas" role="group" :aria-label="t('approval.designer.graph.canvasLabel')" data-testid="de-canvas">
    <VueFlow
      :id="canvasId" :nodes="nodes" :edges="edges" :min-zoom="0.2" :max-zoom="1.5" :fit-view-on-init="!initialNodes?.length" :snap-to-grid="true" :snap-grid="[GRID, GRID]"
      :nodes-draggable="editable" :nodes-connectable="false" :elements-selectable="true" :nodes-focusable="false" :edges-focusable="false" :disable-keyboard-a11y="true"
      :zoom-on-scroll="true" :pan-on-scroll="false" :zoom-on-pinch="true" :pan-on-drag="true" :zoom-on-double-click="false" :delete-key-code="null"
      :selection-key-code="null" :multi-selection-key-code="null"
      @node-click="(e) => emit('node-open', e.node.id)" @edge-click="(e) => emit('edge-open', e.edge.id)" @pane-click="emit('pane-click')" @nodes-initialized="fitInitial"
      @node-drag-stop="(e) => emit('node-move', e.node.id, { x: e.node.position.x, y: e.node.position.y })"
    >
      <template #node-designer="p"><DesignerNode :data="p.data" @open="emit('node-open', p.id)" /></template>
      <template #edge-designer="p"><DesignerEdge v-bind="p" /></template>
    </VueFlow>
    <div class="de-zoom">
      <q-btn dense flat round size="sm" icon="add" :aria-label="t('approval.graph.zoomIn')" :title="t('approval.graph.zoomIn')" data-testid="de-zoom-in" @click="zoomIn" />
      <q-btn dense flat round size="sm" icon="remove" :aria-label="t('approval.graph.zoomOut')" :title="t('approval.graph.zoomOut')" data-testid="de-zoom-out" @click="zoomOut" />
    </div>
  </div>
</template>

<style>
/* 不載入 Vue Flow 的 theme-default.css；線、文字與底色都用主題 token，深色主題一併生效。 */
.de-canvas { position: relative; width: 100%; height: 100%; min-height: 0; }
.de-canvas .vue-flow { background: transparent; }
.de-canvas .vue-flow__pane.draggable { cursor: grab; }
.de-canvas .vue-flow__node { pointer-events: auto; }
.de-canvas .vue-flow__edge { cursor: pointer; }
.de-canvas .vue-flow__edge-path { stroke: var(--t-muted); stroke-width: 1.5; fill: none; }
.de-canvas .de-edge-selected .vue-flow__edge-path { stroke: var(--t-primary); stroke-width: 2.5; }
.de-canvas .de-edge-warn .vue-flow__edge-path { stroke: var(--t-warning); stroke-width: 2.5; }
.de-canvas .de-edge-error .vue-flow__edge-path { stroke: var(--t-negative); stroke-width: 2.5; }
.de-canvas .vue-flow__edge-text { fill: var(--t-text); font-size: 11px; }
.de-canvas .de-edge-hint .vue-flow__edge-text { fill: var(--t-muted); font-style: italic; }
.de-canvas .de-edge-error .vue-flow__edge-text { fill: var(--t-negative); }
.de-canvas .vue-flow__edge-textbg { fill: var(--t-panel); }
.de-canvas .de-zoom {
  position: absolute; top: 4px; right: 4px; z-index: 5; display: flex; gap: 2px; padding: 2px;
  background: var(--t-panel); border: 1px solid var(--t-border); border-radius: var(--t-radius);
}
</style>
