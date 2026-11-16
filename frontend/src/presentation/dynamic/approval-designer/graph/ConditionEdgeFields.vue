<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalEdgeDef } from 'src/domain/dynamic/approval-flow-types';
import { conditionOutEdges, reorderConditionEdge, setEdge } from 'src/domain/dynamic/approval-graph-edit';
import type { ConditionNode, ModuleDetail } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import { fieldErrors, nodeLabel } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 條件節點的一條出線：標籤、條件（預設線沒有）、上移下移。條件節點面板逐條列出，選取單條線時的線面板也用它。
const props = defineProps<{ designer: GraphDesigner; edge: ApprovalEdgeDef; module: ModuleDetail }>();
const { t, locale } = useI18n();
const fields = computed(() => toSchemaFields(props.module));
const def = computed(() => props.designer.state.value!.definition);
const siblings = computed(() => conditionOutEdges(def.value, props.edge.from).filter((e) => !e.default));
const pos = computed(() => siblings.value.findIndex((e) => e.id === props.edge.id));
const isDefault = computed(() => !!props.edge.default);
const target = computed(() => { const n = def.value.nodes.find((x) => x.id === props.edge.to); return n ? nodeLabel(n, locale.value, t) : props.edge.to; });
const errs = computed(() => fieldErrors(props.designer.issues.value?.errors.edges[props.edge.id]));
const edit = computed(() => props.designer.canEdit.value);

const setLabel = (label: Record<string, string>) => props.designer.edit((s) => setEdge(s, props.edge.id, { label: Object.keys(label).length ? label : undefined }), `edge:${props.edge.id}:label`);
const setWhen = (when: ConditionNode | undefined) => props.designer.edit((s) => setEdge(s, props.edge.id, { when }), `edge:${props.edge.id}:when`);
const move = (delta: -1 | 1) => props.designer.edit((s) => reorderConditionEdge(s, props.edge.id, delta));
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" :data-testid="`cond-edge-${edge.id}`">
    <div class="row items-center no-wrap">
      <div class="col text-body2 ellipsis">→ {{ target }}</div>
      <template v-if="!isDefault && edit">
        <q-btn flat dense round size="sm" icon="arrow_upward" :disable="pos <= 0" :aria-label="t('approval.designer.graph.moveUp')" :data-testid="`cond-up-${edge.id}`" @click="move(-1)" />
        <q-btn flat dense round size="sm" icon="arrow_downward" :disable="pos < 0 || pos >= siblings.length - 1" :aria-label="t('approval.designer.graph.moveDown')" :data-testid="`cond-down-${edge.id}`" @click="move(1)" />
      </template>
    </div>
    <I18nTextInput :model-value="edge.label ?? {}" :label="t('approval.designer.graph.edgeLabel')" :hint="t('approval.designer.graph.edgeLabelHint')" optional @update:model-value="setLabel" />
    <div v-if="isDefault" class="text-caption text-muted" :data-testid="`cond-default-${edge.id}`">{{ t('approval.designer.graph.edgeDefaultHint') }}</div>
    <div v-else>
      <ConditionEditor :fields="fields" allow-field-ref :disable="!edit" :model-value="edge.when ?? undefined" @update:model-value="setWhen" />
      <div v-if="errs.when || errs._" class="text-caption text-negative">{{ errs.when ?? errs._ }}</div>
    </div>
  </div>
</template>
