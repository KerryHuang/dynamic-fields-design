<script setup lang="ts">
import { Handle, Position } from '@vue-flow/core';
import type { ProgressNodeData } from './progress-graph';

// 唯讀進度圖的節點。尺寸由排版檔的常數決定（外層 Vue Flow 節點的 style 與這裡的 inline style 讀同一組）。
defineProps<{ data: ProgressNodeData }>();
</script>

<template>
  <div
    class="pg-node" :class="[`pg-frame-${data.frame}`, data.color ? `pg-c-${data.color}` : 'pg-c-none', { 'pg-dim': data.dim, 'pg-small': data.small }]"
    :style="{ width: `${data.width}px`, height: `${data.height}px` }" :title="data.ariaLabel" :aria-label="data.ariaLabel" role="img" data-testid="pg-node"
  >
    <Handle type="target" :position="Position.Left" :connectable="false" class="pg-handle" />
    <div class="pg-head">
      <q-icon :name="data.kindIcon" size="16px" class="pg-kind text-muted" />
      <div class="pg-text">
        <div class="pg-name" :class="{ 'pg-one-line': !!data.joinModeText || data.small }" data-testid="pg-name"><span :class="{ 'pg-strike': data.strike }" data-testid="pg-name-text">{{ data.label }}</span></div>
        <div v-if="data.joinModeText" class="pg-sub text-muted" data-testid="pg-join-mode">{{ data.joinModeText }}</div>
      </div>
    </div>
    <div class="pg-status" data-testid="pg-status">
      <q-icon v-if="data.stateIcon" :name="data.stateIcon" :color="data.color ?? undefined" size="16px" />
      <span v-if="data.statusText" class="pg-status-text">{{ data.statusText }}</span>
      <q-badge v-if="data.overdue" color="negative" class="pg-overdue" :label="data.overdueText" data-testid="pg-overdue" />
    </div>
    <Handle type="source" :position="Position.Right" :connectable="false" class="pg-handle" />
  </div>
</template>

<style scoped>
.pg-node {
  box-sizing: border-box; display: flex; flex-direction: column; justify-content: space-between; overflow: hidden;
  padding: 6px 8px; border: 2px solid var(--t-border); border-radius: var(--t-radius); background: var(--t-panel); color: var(--t-text);
}
.pg-small { padding: 4px 8px; }
.pg-frame-dashed { border-style: dashed; }
.pg-c-positive { border-color: var(--t-positive); }
.pg-c-warning { border-color: var(--t-warning); }
.pg-c-negative { border-color: var(--t-negative); }
.pg-c-primary { border-color: var(--t-primary); }
.pg-c-muted { border-color: var(--t-muted); }
.pg-dim { opacity: 0.55; }
.pg-head { display: flex; align-items: flex-start; gap: 4px; min-height: 0; overflow: hidden; }
.pg-kind { flex: none; margin-top: 1px; }
.pg-text { min-width: 0; flex: 1; }
.pg-name {
  font-size: 14px; line-height: 18px; overflow: hidden; overflow-wrap: anywhere;
  display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2;
}
.pg-name.pg-one-line { -webkit-line-clamp: 1; line-clamp: 1; }
.pg-strike { text-decoration: line-through; text-decoration-thickness: 2px; }
.pg-sub { font-size: 11px; line-height: 14px; }
.pg-status { display: flex; align-items: center; gap: 4px; height: 18px; font-size: 12px; white-space: nowrap; overflow: hidden; }
.pg-status-text { overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.pg-overdue { flex: none; }
.pg-handle { width: 1px; height: 1px; min-width: 0; min-height: 0; border: 0; background: transparent; opacity: 0; pointer-events: none; }
</style>
