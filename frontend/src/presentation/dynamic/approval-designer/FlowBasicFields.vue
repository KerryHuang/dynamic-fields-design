<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApproverSource } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { policyValueText } from 'src/domain/dynamic/policy-types';
import type { ConditionNode, I18nText, ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import { useRecipientNames } from 'src/presentation/policies/useRecipientNames';
import ApproverSourceEditor from './ApproverSourceEditor.vue';
import { defaultSourceOf, FALLBACK_SOURCE_KINDS } from './approver-source';
import { useApprovalLimits } from './use-approval-limits';

/** The flow-level fields shared by the old dialog ("basic" section) and the designer's flow panel. */
export interface FlowBasic {
  key: string;
  name: I18nText;
  toOptionId: number | null;
  when?: ConditionNode;
  rejectTo: number | null;
  /** null inherits the module's default fallback approvers (policy). */
  fallback: ApproverSource | null;
}
export interface FlowBasicErrors { key?: string; name?: string; toOptionId?: string; priority?: string; when?: string; rejectTo?: string; fallback?: string }

const props = defineProps<{
  modelValue: FlowBasic;
  module: ModuleDetail;
  workflow: WorkflowResponse;
  /** The key is fixed once the flow exists. */
  keyReadonly: boolean;
  /** A used flow cannot change its target state. */
  targetLocked: boolean;
  /** The fallback approvers as last saved (the starting point of "custom"). */
  savedFallback?: ApproverSource | null;
  errors?: FlowBasicErrors;
  disable?: boolean;
  /** Shows "takes effect now, not on publish" under the name and the target (the designer saves them with the draft but they apply at once, U53). */
  immediate?: boolean;
  /** 編輯既有流程時載入的名稱：必填語系增加後，沒改名稱不要求補語系。 */
  originalName?: I18nText;
}>();
const emit = defineEmits<{ patch: [p: Partial<FlowBasic>] }>();
const { t, locale } = useI18n();
const err = computed(() => props.errors ?? {});
const { limits, fallbackPolicy } = useApprovalLimits(() => props.module.key);
const recipients = useRecipientNames();
const fields = computed(() => toSchemaFields(props.module));
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
defineExpose({ validate: async () => !!(await nameInput.value?.validate()) });

// ---- states ----
const statusField = computed(() => props.module.blocks.flatMap((b) => b.fields).find((f) => f.id === props.workflow.statusFieldId));
const stateLabel = (id: number) => { const o = statusField.value?.options.find((x) => x.id === id); return o ? resolveI18n(o.label, locale.value) : String(id); };
const hasEntry = (id: number) => props.workflow.transitions.some((tr) => tr.to === id && tr.approval?.required);
const targetOptions = computed(() => props.workflow.states.filter((s) => s.optionId !== props.workflow.initialOptionId).map((s) => ({
  value: s.optionId,
  label: t(hasEntry(s.optionId) ? 'approval.designer.basic.targetHasEntry' : 'approval.designer.basic.targetNoEntry', { state: stateLabel(s.optionId) }),
})));
const rejectOptions = computed(() => [
  { value: null, label: t('approval.designer.basic.rejectStay') },
  ...props.workflow.states.map((s) => ({ value: s.optionId as number | null, label: stateLabel(s.optionId) })),
]);

// ---- fallback: null inherits the module default; switching to inherit and back keeps what was there ----
let lastCustom: ApproverSource | null = null;
const fallbackMode = computed(() => (props.modelValue.fallback ? 'custom' : 'inherit'));
function setFallbackMode(m: 'inherit' | 'custom') {
  if (m === 'inherit') { lastCustom = props.modelValue.fallback ?? lastCustom; emit('patch', { fallback: null }); return; }
  emit('patch', { fallback: lastCustom ?? props.savedFallback ?? defaultSourceOf('users') });
}
const inheritText = computed(() => {
  const p = fallbackPolicy.value;
  const v = p?.effective;
  if (!p || !Array.isArray(v) || !v.length) return t('approval.designer.basic.fallbackInheritNone');
  return t('approval.designer.basic.fallbackInherit', { names: policyValueText(p, v, t, recipients.names.value) });
});
</script>

<template>
  <div class="column no-wrap q-gutter-y-md">
    <q-input
      :model-value="modelValue.key" :label="t('approval.designer.basic.key')" :hint="t('approval.designer.basic.keyHint')" :readonly="keyReadonly" :disable="disable" dense outlined
      data-testid="flow-key" :error="!!err.key || undefined" :error-message="err.key" @update:model-value="(v) => emit('patch', { key: String(v ?? '') })"
    />
    <I18nTextInput ref="nameInput" :model-value="modelValue.name" :original="originalName" :label="t('approval.designer.basic.name')" :error="err.name" @update:model-value="(v) => emit('patch', { name: v })" />
    <div v-if="immediate" class="text-caption text-muted" data-testid="flow-immediate-name">{{ t('approval.designer.graph.immediate') }}</div>
    <q-select
      :model-value="modelValue.toOptionId" :options="targetOptions" :label="t('approval.designer.basic.target')" emit-value map-options dense outlined
      :disable="targetLocked || disable" :hint="targetLocked ? t('approval.designer.basic.targetUsedLocked') : undefined"
      :error="!!err.toOptionId || undefined" :error-message="err.toOptionId" data-testid="flow-target" @update:model-value="(v: number) => emit('patch', { toOptionId: v })"
    />
    <div v-if="immediate" class="text-caption text-muted" data-testid="flow-immediate-target">{{ t('approval.designer.graph.immediate') }}</div>
    <div v-if="err.priority" class="text-caption text-negative">{{ err.priority }}</div>
    <div>
      <div class="text-caption text-muted">{{ t('approval.designer.basic.when') }}</div>
      <ConditionEditor :fields="fields" allow-field-ref :disable="disable" :model-value="modelValue.when" @update:model-value="(v) => emit('patch', { when: v })" />
      <div class="text-caption text-muted">{{ t('approval.designer.basic.whenHint') }}</div>
      <div v-if="err.when" class="text-caption text-negative">{{ err.when }}</div>
    </div>
    <q-select
      :model-value="modelValue.rejectTo" :options="rejectOptions" :label="t('approval.designer.basic.rejectTo')" emit-value map-options dense outlined :disable="disable"
      :error="!!err.rejectTo || undefined" :error-message="err.rejectTo" data-testid="flow-reject" @update:model-value="(v: number | null) => emit('patch', { rejectTo: v })"
    />
    <div class="column no-wrap q-gutter-y-sm" data-testid="flow-fallback">
      <div class="text-subtitle2">{{ t('approval.designer.basic.fallback') }}</div>
      <q-option-group
        style="overflow-wrap: anywhere" :model-value="fallbackMode" type="radio" role="radiogroup" :aria-label="t('approval.designer.basic.fallback')" :disable="disable" data-testid="flow-fallback-mode"
        :options="[{ value: 'inherit', label: inheritText }, { value: 'custom', label: t('approval.designer.basic.fallbackCustom') }]"
        @update:model-value="setFallbackMode"
      />
      <ApproverSourceEditor
        v-if="modelValue.fallback" :model-value="modelValue.fallback" :module="module" :allowed="FALLBACK_SOURCE_KINDS" :limits="limits" fallback :disable="disable"
        :saved="savedFallback ?? undefined" :label="t('approval.designer.basic.fallbackSource')" :error="err.fallback" @update:model-value="(v) => emit('patch', { fallback: v })"
      />
      <div v-else-if="err.fallback" class="text-caption text-negative">{{ err.fallback }}</div>
    </div>
  </div>
</template>
