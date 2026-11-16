<script setup lang="ts">
import { Handle, Position } from '@vue-flow/core';
import { useI18n } from 'vue-i18n';
import type { DesignerNodeData } from './designer-graph';

// 設計頁的節點（J76）。尺寸由外層 Vue Flow 節點的 style 決定（排版檔的常數）；顏色只用語意 token，型別另有圖示與文字（§10.3）。
defineProps<{ data: DesignerNodeData }>();
const emit = defineEmits<{ open: [] }>();
const { t } = useI18n();
</script>

<template>
  <div
    class="de-node" :class="[`de-kind-${data.kind}`, { 'de-selected': data.selected, 'de-has-error': data.errors > 0, 'de-has-warn': data.errors === 0 && data.warnings > 0 }]"
    role="button" tabindex="0" :aria-label="data.ariaLabel" :aria-pressed="data.selected" data-testid="de-node"
    @keydown.enter.prevent="emit('open')" @keydown.space.prevent="emit('open')"
  >
    <Handle type="target" :position="Position.Left" :connectable="false" class="de-handle" />
    <div class="de-head">
      <q-icon :name="data.icon" size="16px" class="de-kind text-muted" />
      <div class="de-name" data-testid="de-name">{{ data.label }}</div>
    </div>
    <div class="de-summary text-muted" data-testid="de-summary">{{ data.summary }}</div>
    <div class="de-badges">
      <q-badge v-if="data.skip" outline color="primary" :label="t('approval.designer.graph.skipBadge')" data-testid="de-skip" />
      <q-badge v-if="data.errors" color="negative" :label="data.errors" :aria-label="t('approval.designer.graph.errorCount', { n: data.errors })" data-testid="de-errors" />
      <q-badge v-if="data.warnings" color="warning" text-color="dark" :label="data.warnings" :aria-label="t('approval.designer.graph.warningCount', { n: data.warnings })" data-testid="de-warnings" />
    </div>
    <Handle type="source" :position="Position.Right" :connectable="false" class="de-handle" />
  </div>
</template>

<style scoped>
.de-node {
  box-sizing: border-box; display: flex; flex-direction: column; justify-content: space-between; width: 100%; height: 100%; overflow: hidden; padding: 6px 8px;
  border: 2px solid var(--t-border); border-radius: var(--t-radius); background: var(--t-panel); color: var(--t-text); cursor: pointer; pointer-events: auto;
}
.de-kind-start, .de-kind-end { border-style: dashed; }
.de-node:focus-visible { outline: 2px solid var(--t-primary); outline-offset: 2px; }
.de-selected { border-color: var(--t-primary); box-shadow: 0 0 0 2px var(--t-primary); }
.de-has-error { border-color: var(--t-negative); }
.de-has-warn { border-color: var(--t-warning); }
.de-head { display: flex; align-items: flex-start; gap: 4px; min-height: 0; overflow: hidden; }
.de-kind { flex: none; margin-top: 1px; }
.de-name { font-size: 14px; line-height: 18px; overflow: hidden; overflow-wrap: anywhere; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 1; line-clamp: 1; }
.de-summary { font-size: 11px; line-height: 14px; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.de-badges { display: flex; gap: 4px; height: 18px; overflow: hidden; }
.de-handle { width: 1px; height: 1px; min-width: 0; min-height: 0; border: 0; background: transparent; opacity: 0; pointer-events: none; }
</style>
