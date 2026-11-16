<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { isSingleApprover } from 'src/domain/dynamic/approval-flow-draft';
import type { ApproverSource } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, I18nText, ModuleDetail } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import ApproverSourceEditor from './ApproverSourceEditor.vue';
import { ALL_SOURCE_KINDS } from './approver-source';
import type { FlowLimits } from './use-approval-limits';

/** The fields one approval step carries, shared by a stage card (sequence) and an approval node (graph); the code is not here, each editor places it. */
export interface StageFields {
  name: I18nText;
  when?: ConditionNode;
  approvers: ApproverSource;
  mode: 'all' | 'any';
  /** The mode the designer chose while the source was a single person (which forces `any`); restored when the source stops being one. */
  chosenMode?: 'all' | 'any';
  editableFields: number[];
  dueHours: number | null;
  onEmpty: 'fallback' | 'skip';
}

const props = defineProps<{
  modelValue: StageFields;
  module: ModuleDetail;
  /** Fields a step may make editable (from the workflow response). */
  editableFieldIds: number[];
  limits: FlowLimits;
  /** The approvers as last saved: a manager level above the policy max stays selectable on a step that already had it. */
  savedApprovers?: ApproverSource;
  errors?: Record<string, string>;
  disable?: boolean;
  /** Suffix of the data-testid values (stage index, or a node code). */
  idx: number | string;
}>();
const emit = defineEmits<{ patch: [p: Partial<StageFields>] }>();
const { t, locale } = useI18n();

const fields = computed(() => toSchemaFields(props.module));
const allFields = computed(() => props.module.blocks.flatMap((b) => b.fields));
const fieldLabel = (id: number) => { const f = allFields.value.find((x) => x.id === id); return f ? resolveI18n(f.label, locale.value) : String(id); };
const editableOptions = computed(() => props.editableFieldIds.map((id) => ({ value: id, label: fieldLabel(id) })));
// A saved field that is no longer a candidate stays visible (as its name) so the designer sees what the step still carries.
const editableOptionsOf = computed(() => [...editableOptions.value, ...props.modelValue.editableFields.filter((id) => !props.editableFieldIds.includes(id)).map((id) => ({ value: id, label: fieldLabel(id) }))]);
const err = computed(() => props.errors ?? {});
const dueNum = (v: string | number | null) => (v === null || v === '' ? null : Number(v));

// A single-person source forces mode any; the chosen mode is kept aside and comes back when the source is a group again.
function setApprovers(v: ApproverSource) {
  const s = props.modelValue;
  if (isSingleApprover(v)) emit('patch', { approvers: v, mode: 'any', chosenMode: s.chosenMode ?? (isSingleApprover(s.approvers) ? undefined : s.mode) });
  else emit('patch', { approvers: v, mode: s.chosenMode ?? s.mode, chosenMode: undefined });
}
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm">
    <I18nTextInput :model-value="modelValue.name" :label="t('approval.designer.stage.name')" :error="err.name" @update:model-value="(v) => emit('patch', { name: v })" />
    <slot name="after-name" />

    <div>
      <div class="text-caption text-muted">{{ t('approval.designer.stage.when') }}</div>
      <ConditionEditor :fields="fields" allow-field-ref :model-value="modelValue.when" @update:model-value="(v) => emit('patch', { when: v })" />
      <div class="text-caption text-muted">{{ t('approval.designer.stage.whenHint') }}</div>
      <div v-if="err.when" class="text-caption text-negative">{{ err.when }}</div>
    </div>

    <ApproverSourceEditor
      :model-value="modelValue.approvers" :module="module" :allowed="ALL_SOURCE_KINDS" :limits="limits" :saved="savedApprovers"
      :label="t('approval.designer.stage.source')" :error="err.approvers" :disable="disable" @update:model-value="setApprovers"
    />

    <q-option-group
      v-if="!isSingleApprover(modelValue.approvers)" :model-value="modelValue.mode" type="radio" inline :disable="disable" role="radiogroup" :aria-label="t('approval.designer.stage.mode')"
      :options="[{ value: 'all', label: t('approval.designer.stage.modeAll') }, { value: 'any', label: t('approval.designer.stage.modeAny') }]"
      :data-testid="`stage-mode-${idx}`" @update:model-value="(v: 'all' | 'any') => emit('patch', { mode: v })"
    />
    <div v-if="err.mode" class="text-caption text-negative">{{ err.mode }}</div>

    <q-select
      :model-value="modelValue.editableFields" :options="editableOptionsOf" :label="t('approval.designer.stage.editableFields')" multiple use-chips emit-value map-options dense outlined
      :disable="disable" :hint="t('approval.designer.stage.editableFieldsHint')" :error="!!err.editableFields || undefined" :error-message="err.editableFields"
      :data-testid="`stage-editable-${idx}`" @update:model-value="(v: number[]) => emit('patch', { editableFields: v })"
    />

    <q-input
      :model-value="modelValue.dueHours ?? ''" type="number" min="1" :max="limits.maxDueHours" :label="t('approval.designer.stage.dueHours')" dense outlined :disable="disable"
      :hint="limits.maxDueHours === undefined ? t('approval.designer.stage.dueHoursHint') : t('approval.designer.stage.dueHoursHintMax', { max: limits.maxDueHours })"
      :error="!!err.dueHours || undefined" :error-message="err.dueHours" :data-testid="`stage-due-${idx}`"
      @update:model-value="(v) => emit('patch', { dueHours: dueNum(v) })"
    />

    <q-option-group
      :model-value="modelValue.onEmpty" type="radio" inline :disable="disable" role="radiogroup" :aria-label="t('approval.designer.stage.onEmpty')"
      :options="[{ value: 'fallback', label: t('approval.designer.stage.onEmptyFallback') }, { value: 'skip', label: t('approval.designer.stage.onEmptySkip') }]"
      :data-testid="`stage-onempty-${idx}`" @update:model-value="(v: 'fallback' | 'skip') => emit('patch', { onEmpty: v })"
    />
    <div v-if="modelValue.onEmpty === 'skip'" class="text-caption text-warning" :data-testid="`stage-skip-warning-${idx}`">{{ t('approval.designer.stage.skipWarning') }}</div>
    <div v-if="err.onEmpty" class="text-caption text-negative">{{ err.onEmpty }}</div>
    <div v-if="err._" class="text-caption text-negative" :data-testid="`stage-error-${idx}`">{{ err._ }}</div>
  </div>
</template>
