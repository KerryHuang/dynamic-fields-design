<script setup lang="ts">
import { Handle, Position } from '@vue-flow/core';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { joinList } from 'src/i18n/translate';
import type { DesignerNodeData } from './designer-graph';

// 設計頁的節點（J76）。尺寸由外層 Vue Flow 節點的 style 決定（排版檔的常數）；顏色只用語意 token，型別另有圖示與文字（§10.3）。
const props = defineProps<{ data: DesignerNodeData }>();
const emit = defineEmits<{ open: [] }>();
const { t, locale } = useI18n();

// 試算疊圖（J80）：簽核節點在摘要列改寫成解析到的簽核人；不在路徑上的節點維持原摘要（只淡化）。
const trial = computed(() => props.data.trial ?? null);
const summary = computed(() => {
  const tr = trial.value;
  if (!tr || tr.state === 'off') return props.data.summary;
  if (tr.state === 'skipped') return t('approval.submit.stageSkip');
  if (tr.state === 'undecided') return t('approval.submit.stageConditional');
  if (tr.approvers.length) return joinList(tr.approvers, locale.value);
  return tr.pick ? t('approval.designer.trial.stagePick') : props.data.summary;
});
</script>

<template>
  <div
    class="de-node" :class="[`de-kind-${data.kind}`, trial && `de-trial-${trial.state}`, { 'de-selected': data.selected, 'de-has-error': data.errors > 0, 'de-has-warn': data.errors === 0 && data.warnings > 0 }]"
    role="button" tabindex="0" :aria-label="data.ariaLabel" :aria-pressed="data.selected" data-testid="de-node"
    @keydown.enter.prevent="emit('open')" @keydown.space.prevent="emit('open')"
  >
    <Handle type="target" :position="Position.Left" :connectable="false" class="de-handle" />
    <div class="de-head">
      <q-icon :name="data.icon" size="16px" class="de-kind text-muted" />
      <div class="de-name" data-testid="de-name">{{ data.label }}</div>
    </div>
    <div class="de-summary text-muted" :title="summary" data-testid="de-summary">{{ summary }}</div>
    <div class="de-badges">
      <template v-if="trial && trial.state !== 'off'">
        <q-badge
          v-if="trial.problem" color="negative" :label="t(`approval.designer.trialPanel.problemShort.${trial.problem}`)" :title="t(`approval.designer.trial.problem.${trial.problem}`)"
          data-testid="de-trial-problem"
        />
        <q-badge v-if="trial.anyGroup" outline color="primary" :label="t('approval.submit.stageAnyBranch')" :title="t('approval.submit.stageAnyGroup')" data-testid="de-trial-any" />
        <q-badge v-if="trial.fallback" outline color="warning" :label="t('approval.submit.stageFallback')" data-testid="de-trial-fallback" />
      </template>
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
/* 試算疊圖：走到的用主色框、不走的淡化、略過的淡化加虛線、停在條件節點的主色虛線；全部只用語意 token 與透明度。 */
.de-trial-path { border-color: var(--t-primary); }
.de-trial-off { opacity: 0.35; }
.de-trial-skipped { opacity: 0.6; border-style: dashed; }
.de-trial-undecided { border-color: var(--t-primary); border-style: dashed; }
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
