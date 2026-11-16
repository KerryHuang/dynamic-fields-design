<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import type { ApprovalNodeDef } from 'src/domain/dynamic/approval-flow-types';
import { setNode } from 'src/domain/dynamic/approval-graph-edit';
import type { GraphDesigner } from './use-graph-designer';

// 匯合節點（畫布上既有的）：全部到齊或任一到齊；任一時說明其他分支會被取消。
const props = defineProps<{ designer: GraphDesigner; node: ApprovalNodeDef }>();
const { t } = useI18n();
const mode = () => (props.node.mode === 'any' ? 'any' : 'all');
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" data-testid="join-node-panel">
    <q-option-group
      :model-value="mode()" type="radio" role="radiogroup" :aria-label="t('approval.designer.graph.joinMode')" :disable="!designer.canEdit.value"
      :options="[{ label: t('approval.graph.joinMode.all'), value: 'all' }, { label: t('approval.graph.joinMode.any'), value: 'any' }]"
      data-testid="join-mode" @update:model-value="(v: 'all' | 'any') => designer.edit((s) => setNode(s, node.id, { mode: v }))"
    />
    <div v-if="mode() === 'any'" class="text-caption text-muted" data-testid="join-any-hint">{{ t('approval.designer.graph.joinAnyHint') }}</div>
  </div>
</template>
