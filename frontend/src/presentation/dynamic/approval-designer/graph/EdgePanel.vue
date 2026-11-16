<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalEdgeDef } from 'src/domain/dynamic/approval-flow-types';
import { setEdge } from 'src/domain/dynamic/approval-graph-edit';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import ConditionEdgeFields from './ConditionEdgeFields.vue';
import { fieldErrors } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 線的屬性：標籤（顯示在線上，條件出線沒有標籤時改顯示條件摘要）；從條件節點拉出的線另有條件與順序。
const props = defineProps<{ designer: GraphDesigner; edge: ApprovalEdgeDef; module: ModuleDetail }>();
const { t } = useI18n();
const fromCondition = computed(() => props.designer.state.value?.definition.nodes.find((n) => n.id === props.edge.from)?.kind === 'condition');
const errs = computed(() => fieldErrors(props.designer.issues.value?.errors.edges[props.edge.id]));
function setLabel(label: Record<string, string>) {
  props.designer.edit((s) => setEdge(s, props.edge.id, { label: Object.keys(label).length ? label : undefined }), `edge:${props.edge.id}:label`);
}
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" data-testid="edge-panel">
    <ConditionEdgeFields v-if="fromCondition" :designer="designer" :edge="edge" :module="module" />
    <I18nTextInput v-else :model-value="edge.label ?? {}" :label="t('approval.designer.graph.edgeLabel')" :hint="t('approval.designer.graph.edgeLabelHint')" optional @update:model-value="setLabel" />
    <div v-if="errs._" class="text-caption text-negative">{{ errs._ }}</div>
    <slot />
  </div>
</template>
