<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalNodeDef } from 'src/domain/dynamic/approval-flow-types';
import { setNode } from 'src/domain/dynamic/approval-graph-edit';
import type { ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import ApprovalStageFields, { type StageFields } from '../ApprovalStageFields.vue';
import { defaultSourceOf } from '../approver-source';
import { useApprovalLimits } from '../use-approval-limits';
import { fieldErrors } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 簽核節點屬性：欄位與舊對話框的關卡卡片共用（ApprovalStageFields）；要依欄位走不同的人，請用條件節點（§7.3）。
const props = defineProps<{ designer: GraphDesigner; node: ApprovalNodeDef; module: ModuleDetail; workflow: WorkflowResponse }>();
const { t } = useI18n();
const { limits } = useApprovalLimits(() => props.module.key);
// 單一人員來源強制「或簽」時，原本選的方式記在這裡，改回群組時還原（與舊對話框相同，不送出）。
const chosen = ref<Record<string, 'all' | 'any'>>({});

const model = computed<StageFields>(() => ({
  name: props.node.name ?? {}, when: props.node.when ?? undefined, approvers: props.node.approvers ?? defaultSourceOf('manager'),
  mode: props.node.mode === 'all' ? 'all' : 'any', chosenMode: chosen.value[props.node.id], editableFields: props.node.editableFields ?? [],
  dueHours: props.node.dueHours ?? null, onEmpty: props.node.onEmpty ?? 'fallback',
}));
const errs = computed(() => fieldErrors(props.designer.issues.value?.errors.nodes[props.node.id]));

function onPatch(p: Partial<StageFields>) {
  const { chosenMode, ...rest } = p;
  if ('chosenMode' in p) {
    if (chosenMode) chosen.value = { ...chosen.value, [props.node.id]: chosenMode };
    else { const { [props.node.id]: _gone, ...others } = chosen.value; chosen.value = others; }
  }
  const key = 'name' in rest ? `node:${props.node.id}:name` : 'dueHours' in rest ? `node:${props.node.id}:due` : undefined;
  props.designer.edit((s) => setNode(s, props.node.id, rest), key);
}
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" data-testid="approval-node-panel">
    <div class="text-caption text-muted">{{ t('approval.designer.graph.approvalHint') }}</div>
    <ApprovalStageFields
      :model-value="model" :module="module" :editable-field-ids="workflow.editableFieldIds ?? []" :limits="limits"
      :saved-approvers="designer.server.value.publishedApprovers[node.id]" :errors="errs" :disable="!designer.canEdit.value" :idx="node.id" @patch="onPatch"
    />
  </div>
</template>
