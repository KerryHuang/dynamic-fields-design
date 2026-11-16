<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalNodeDef } from 'src/domain/dynamic/approval-flow-types';
import { conditionOutEdges, setNode } from 'src/domain/dynamic/approval-graph-edit';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import ConditionEdgeFields from './ConditionEdgeFields.vue';
import type { GraphDesigner } from './use-graph-designer';

// 條件節點：名稱，加出線清單（標籤、條件、上移下移；預設線固定最後、不能設條件，§10.4）。
const props = defineProps<{ designer: GraphDesigner; node: ApprovalNodeDef; module: ModuleDetail }>();
const { t } = useI18n();
const out = computed(() => conditionOutEdges(props.designer.state.value!.definition, props.node.id));
const setName = (name: Record<string, string>) => props.designer.edit((s) => setNode(s, props.node.id, { name: Object.keys(name).length ? name : undefined }), `node:${props.node.id}:name`);
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="condition-node-panel">
    <I18nTextInput :model-value="node.name ?? {}" :label="t('approval.designer.stage.name')" optional @update:model-value="setName" />
    <div class="text-subtitle2">{{ t('approval.designer.graph.condOutEdges') }}</div>
    <div v-if="!out.length" class="text-muted" data-testid="cond-no-edges">{{ t('approval.designer.graph.condNoEdges') }}</div>
    <template v-for="(e, i) in out" :key="e.id">
      <q-separator v-if="i > 0" />
      <ConditionEdgeFields :designer="designer" :edge="e" :module="module" />
    </template>
  </div>
</template>
