<script setup lang="ts">
import { computed } from 'vue';
import type { ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import FlowBasicFields, { type FlowBasic } from '../FlowBasicFields.vue';
import { flowErrors } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 流程層屬性：基本欄位與舊對話框共用（FlowBasicFields）。名稱、目標狀態隨存草稿一起送、立即生效（U53），所以旁邊標示。
const props = defineProps<{ designer: GraphDesigner; module: ModuleDetail; workflow: WorkflowResponse }>();
const d = props.designer;
const model = computed<FlowBasic>(() => ({
  key: d.meta.value!.key, name: d.meta.value!.name, toOptionId: d.meta.value!.toOptionId,
  when: d.state.value!.definition.when ?? undefined, rejectTo: d.state.value!.definition.rejectTo ?? null, fallback: d.state.value!.definition.fallbackApprovers ?? null,
}));
const errs = computed(() => flowErrors(d.issues.value?.errors.flow));

function onPatch(p: Partial<FlowBasic>) {
  const { key, name, toOptionId, when, rejectTo, fallback } = p;
  if (key !== undefined) d.editMeta({ key }, 'flow:key');
  if (name !== undefined) d.editMeta({ name }, 'flow:name');
  if (toOptionId !== undefined) d.editMeta({ toOptionId });
  if ('when' in p) d.edit((s) => ({ ...s, definition: { ...s.definition, when } }), 'flow:when');
  if (rejectTo !== undefined) d.edit((s) => ({ ...s, definition: { ...s.definition, rejectTo } }));
  if (fallback !== undefined) d.edit((s) => ({ ...s, definition: { ...s.definition, fallbackApprovers: fallback } }));
}
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="flow-props-panel">
    <div v-if="errs.other.length" class="text-negative" data-testid="flow-props-errors"><div v-for="(m, i) in errs.other" :key="i">{{ m }}</div></div>
    <FlowBasicFields
      :model-value="model" :module="module" :workflow="workflow" :key-readonly="!designer.server.value.isNew" :target-locked="designer.server.value.used"
      :errors="errs.basic" :disable="!designer.canEdit.value" immediate @patch="onPatch"
    />
  </div>
</template>
