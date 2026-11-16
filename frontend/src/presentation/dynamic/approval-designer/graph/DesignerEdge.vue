<script setup lang="ts">
import { BaseEdge, getSmoothStepPath, type EdgeProps } from '@vue-flow/core';
import { computed, h } from 'vue';
import type { DesignerEdgeData } from './designer-graph';

// 設計頁的線（J74）：內建 smoothstep，轉折點由 designer-graph 依座標算好（data.bendX），與進度圖同一條規則。
// 標籤被截斷時，完整文字放進 SVG <title>（滑鼠移上去可看）。
const props = defineProps<EdgeProps<DesignerEdgeData>>();
const geometry = computed(() => getSmoothStepPath({
  sourceX: props.sourceX, sourceY: props.sourceY, sourcePosition: props.sourcePosition,
  targetX: props.targetX, targetY: props.targetY, targetPosition: props.targetPosition, centerX: props.data?.bendX,
}));
const short = computed(() => (typeof props.label === 'string' ? props.label : ''));
const label = computed(() => {
  const full = props.data?.fullLabel ?? short.value;
  return short.value && full !== short.value ? () => [h('title', full), short.value] : short.value || undefined;
});
</script>

<template>
  <BaseEdge :id="id" :path="geometry[0]" :label-x="geometry[1]" :label-y="geometry[2]" :label="label" :interaction-width="20" :label-bg-padding="[4, 2]" />
</template>
