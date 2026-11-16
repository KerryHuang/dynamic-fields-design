<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import ApprovalNodePanel from './ApprovalNodePanel.vue';
import ConditionNodePanel from './ConditionNodePanel.vue';
import EdgePanel from './EdgePanel.vue';
import FlowPropsPanel from './FlowPropsPanel.vue';
import ForkNodePanel from './ForkNodePanel.vue';
import JoinNodePanel from './JoinNodePanel.vue';
import NodeAdvancedFields from './NodeAdvancedFields.vue';
import NotifyNodePanel from './NotifyNodePanel.vue';
import { fieldErrors, nodeLabel, type Selection } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 屬性面板：依選取切換內容。外殼（右側固定面板、平板抽屜、手機全螢幕對話框，§10.5）由設計頁決定。
const props = defineProps<{ designer: GraphDesigner; selection: Selection; module: ModuleDetail; workflow: WorkflowResponse; closable: boolean }>();
const emit = defineEmits<{ close: [] }>();
const { t, locale } = useI18n();
const def = computed(() => props.designer.state.value?.definition);
const node = computed(() => (props.selection.kind === 'node' ? def.value?.nodes.find((n) => n.id === (props.selection as { id: string }).id) : undefined));
const edge = computed(() => (props.selection.kind === 'edge' ? def.value?.edges.find((e) => e.id === (props.selection as { id: string }).id) : undefined));
const title = computed(() => {
  if (node.value) return nodeLabel(node.value, locale.value, t);
  if (edge.value) return t('approval.designer.graph.edgeTitle');
  return t('approval.designer.graph.flowProps');
});
const nodeErrors = computed(() => (node.value ? fieldErrors(props.designer.issues.value?.errors.nodes[node.value.id])._ : undefined));
</script>

<template>
  <div class="column no-wrap de-panel-body" data-testid="de-panel">
    <div class="row items-center no-wrap q-pa-sm">
      <div class="col text-subtitle1 ellipsis" data-testid="de-panel-title">{{ title }}</div>
      <q-btn v-if="closable" flat dense round icon="close" :aria-label="t('approval.designer.graph.close')" data-testid="de-panel-close" @click="emit('close')" />
    </div>
    <q-separator />
    <div class="col scroll q-pa-sm" :style="{ minHeight: 0 }">
      <fieldset class="de-fieldset" :disabled="!designer.canEdit.value">
        <div v-if="nodeErrors" class="text-negative q-mb-sm">{{ nodeErrors }}</div>
        <FlowPropsPanel v-if="selection.kind === 'flow'" :designer="designer" :module="module" :workflow="workflow" />
        <EdgePanel v-else-if="edge" :designer="designer" :edge="edge" :module="module" />
        <template v-else-if="node">
          <ApprovalNodePanel v-if="node.kind === 'approval'" :designer="designer" :node="node" :module="module" :workflow="workflow" />
          <ConditionNodePanel v-else-if="node.kind === 'condition'" :designer="designer" :node="node" :module="module" />
          <ForkNodePanel v-else-if="node.kind === 'fork'" :designer="designer" :node="node" />
          <JoinNodePanel v-else-if="node.kind === 'join'" :designer="designer" :node="node" />
          <NotifyNodePanel v-else-if="node.kind === 'notify'" />
          <div v-else class="text-muted" data-testid="no-props">{{ t('approval.graph.kind.' + node.kind) }}</div>
          <NodeAdvancedFields class="q-mt-md" :designer="designer" :node="node" />
        </template>
      </fieldset>
    </div>
  </div>
</template>

<style scoped>
.de-fieldset { border: 0; margin: 0; padding: 0; min-width: 0; }
</style>
