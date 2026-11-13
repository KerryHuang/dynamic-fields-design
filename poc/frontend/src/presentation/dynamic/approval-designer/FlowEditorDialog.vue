<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useSaveApprovalFlow } from 'src/application/dynamic/queries/approval-flow-queries';
import {
  buildFlowInput, definitionChanged, draftFromFlow, emptyFlowDraft, nextPriority, type FlowDraft,
} from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow, ApproverSource } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { policyValueText } from 'src/domain/dynamic/policy-types';
import type { ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import { useRecipientNames } from 'src/presentation/policies/useRecipientNames';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { errorFor } from 'src/presentation/dynamic/designer/api-error';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import type { ApiError } from 'src/services/api/http';
import ApproverSourceEditor from './ApproverSourceEditor.vue';
import { defaultSourceOf, FALLBACK_SOURCE_KINDS } from './approver-source';
import FlowStagesEditor from './FlowStagesEditor.vue';
import { useApprovalLimits } from './use-approval-limits';

const props = defineProps<{
  modelValue: boolean;
  module: ModuleDetail;
  workflow: WorkflowResponse;
  flows: ApprovalFlow[];
  flow?: ApprovalFlow;
  presetTarget?: number | null;
}>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean]; saved: [flow: ApprovalFlow | undefined] }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const mobile = computed(() => $q.screen.lt.sm);

const isEdit = computed(() => !!props.flow);
const section = ref<'basic' | 'stages'>('basic');
const form = ref<FlowDraft>(emptyFlowDraft(null, 1));
const error = ref<ApiError | null>(null);
const targetMissing = ref(false);
// Switching the fallback to inherit and back keeps what was there (the draft's custom value, else the saved one).
let lastCustom: ApproverSource | null = null;
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);

watch(() => [props.modelValue, props.flow?.key, props.presetTarget] as const, ([open]) => {
  if (!open) return;
  section.value = 'basic';
  targetMissing.value = false;
  const f = props.flow;
  const preset = props.presetTarget ?? null;
  form.value = f ? draftFromFlow(f) : emptyFlowDraft(preset, preset === null ? 1 : nextPriority(props.flows, preset));
  error.value = null;
  lastCustom = null;
}, { immediate: true });
// Server errors point at indexes and values that an edit moves, so any edit clears them (as the rules tab does).
watch(form, () => { error.value = null; }, { deep: true });

const { limits, fallbackPolicy } = useApprovalLimits(() => props.module.key);
const recipients = useRecipientNames();

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
const targetLocked = computed(() => !!props.flow?.used);

function setTarget(id: number) {
  form.value.toOptionId = id;
  targetMissing.value = false;
  // Same target as saved keeps its place; a new target goes to the end of that target's list.
  form.value.priority = props.flow && props.flow.toOptionId === id ? props.flow.priority : nextPriority(props.flows, id, props.flow?.key);
}

// ---- fallback: null inherits the module default ----
const fallbackMode = computed(() => (form.value.fallback ? 'custom' : 'inherit'));
const setFallbackMode = (m: 'inherit' | 'custom') => {
  if (m === 'inherit') { lastCustom = form.value.fallback ?? lastCustom; form.value.fallback = null; return; }
  form.value.fallback = lastCustom ?? props.flow?.definition.fallbackApprovers ?? defaultSourceOf('users');
};
const inheritText = computed(() => {
  const p = fallbackPolicy.value;
  const v = p?.effective;
  if (!p || !Array.isArray(v) || !v.length) return t('approval.designer.basic.fallbackInheritNone');
  return t('approval.designer.basic.fallbackInherit', { names: policyValueText(p, v, t, recipients.names.value) });
});

// ---- banner ----
const versionUsed = computed(() => !!props.flow && (props.flow.currentVersionUsed ?? props.flow.used));
const targetChanged = computed(() => !!props.flow && form.value.toOptionId !== props.flow.toOptionId);
const banner = computed(() => {
  const f = props.flow;
  if (f && versionUsed.value && definitionChanged(form.value, f)) {
    return f.pendingCount > 0
      ? t('approval.designer.dialog.versionBanner', { next: f.version + 1, n: f.pendingCount })
      : t('approval.designer.dialog.versionBannerNone', { next: f.version + 1 });
  }
  return !f || targetChanged.value ? t('approval.designer.dialog.afterOnly') : undefined;
});

// ---- save ----
const save = useSaveApprovalFlow(() => props.module.key);
const fields = computed(() => toSchemaFields(props.module));
const errs = computed(() => error.value?.errors);
const stageKey = /^definition\.stages\[\d+\]/;
const own = /^(key|name|toOptionId|priority|definition\.(when|rejectTo|fallbackApprovers))(\.|\[|$)/;
const keyError = computed(() => errorFor(error.value, 'key') ?? (error.value?.status === 409 && !isEdit.value && !Object.keys(errs.value ?? {}).length ? error.value.message : undefined));
const nameError = computed(() => errorFor(error.value, 'name'));
const targetError = computed(() => (targetMissing.value ? t('approval.designer.basic.targetRequired') : errorFor(error.value, 'toOptionId')));
const whenError = computed(() => errorFor(error.value, 'definition.when'));
const rejectError = computed(() => errorFor(error.value, 'definition.rejectTo'));
const fallbackError = computed(() => errorFor(error.value, 'definition.fallbackApprovers'));
const priorityError = computed(() => errorFor(error.value, 'priority'));
const topErrors = computed(() => Object.entries(errs.value ?? {}).filter(([k]) => !own.test(k) && !stageKey.test(k)).flatMap(([, v]) => v));
const stageErrorCount = computed(() => Object.keys(errs.value ?? {}).filter((k) => stageKey.test(k)).length);

async function submit() {
  if (!(await nameInput.value?.validate())) { section.value = 'basic'; return; }
  const body = buildFlowInput(form.value);
  if (!body) { targetMissing.value = true; section.value = 'basic'; return; }
  error.value = null;
  save.mutate({ key: props.flow?.key, input: body }, {
    onSuccess: (r) => {
      const saved = r as ApprovalFlow | undefined;
      emit('saved', saved);
      emit('update:modelValue', false);
      const bumped = !!props.flow && !!saved?.version && saved.version !== props.flow.version;
      $q.notify({ type: 'positive', message: bumped ? t('approval.designer.dialog.savedVersion', { v: saved!.version }) : t('approval.designer.dialog.saved') });
    },
    onError: (e) => {
      const err = e as unknown as ApiError;
      error.value = err;
      const keys = Object.keys(err.errors ?? {});
      if (!keys.length && !(err.status === 409 && !isEdit.value)) $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
      // Show where the problem is: any basic-section error first, otherwise the stages.
      // Only a stage error moves to the stages section; constraint errors (transitions[..], flows[..]) are listed at the top of whichever section is open.
      section.value = keys.some((k) => stageKey.test(k)) && !keys.some((k) => own.test(k)) ? 'stages' : 'basic';
    },
  });
}
const title = computed(() => (isEdit.value ? t('approval.designer.dialog.edit') : t('approval.designer.dialog.create')));
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="mobile" aria-labelledby="flow-dialog-title" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap flow-dialog" :class="{ 'flow-dialog--desktop': !mobile }" data-testid="flow-dialog">
      <q-card-section id="flow-dialog-title" class="text-h6 q-pb-none">{{ title }}</q-card-section>
      <div :class="mobile ? 'column no-wrap col' : 'row no-wrap col'" style="min-height: 0">
        <q-tabs
          v-model="section" :vertical="!mobile" dense align="left" :class="mobile ? '' : 'flow-dialog__side'" :aria-label="title" data-testid="flow-sections"
        >
          <q-tab name="basic" :label="t('approval.designer.dialog.sectionBasic')" data-testid="section-basic" />
          <q-tab name="stages" :label="t('approval.designer.dialog.sectionStages')" data-testid="section-stages">
            <q-badge v-if="stageErrorCount" color="negative" floating rounded :label="stageErrorCount" />
          </q-tab>
        </q-tabs>
        <q-separator :vertical="!mobile" />
        <div class="col scroll q-pa-md" style="min-width: 0">
          <q-banner v-if="banner" dense rounded class="bg-info-soft q-mb-md" data-testid="flow-banner">{{ banner }}</q-banner>
          <div v-if="topErrors.length" class="text-negative q-mb-md" data-testid="flow-top-errors">
            <div v-for="(m, i) in topErrors" :key="i">{{ m }}</div>
          </div>

          <div v-show="section === 'basic'" class="column no-wrap q-gutter-y-md" data-testid="flow-basic">
            <q-input
              v-model="form.key" :label="t('approval.designer.basic.key')" :hint="t('approval.designer.basic.keyHint')" :readonly="isEdit" dense outlined
              data-testid="flow-key" :error="!!keyError || undefined" :error-message="keyError"
            />
            <I18nTextInput ref="nameInput" v-model="form.name" :label="t('approval.designer.basic.name')" :error="nameError" />
            <q-select
              :model-value="form.toOptionId" :options="targetOptions" :label="t('approval.designer.basic.target')" emit-value map-options dense outlined
              :disable="targetLocked" :hint="targetLocked ? t('approval.designer.basic.targetUsedLocked') : undefined"
              :error="!!targetError || undefined" :error-message="targetError" data-testid="flow-target" @update:model-value="setTarget"
            />
            <div v-if="priorityError" class="text-caption text-negative">{{ priorityError }}</div>
            <div>
              <div class="text-caption text-muted">{{ t('approval.designer.basic.when') }}</div>
              <ConditionEditor :fields="fields" allow-field-ref :model-value="form.when" @update:model-value="(v) => { form.when = v; }" />
              <div class="text-caption text-muted">{{ t('approval.designer.basic.whenHint') }}</div>
              <div v-if="whenError" class="text-caption text-negative">{{ whenError }}</div>
            </div>
            <q-select
              v-model="form.rejectTo" :options="rejectOptions" :label="t('approval.designer.basic.rejectTo')" emit-value map-options dense outlined
              :error="!!rejectError || undefined" :error-message="rejectError" data-testid="flow-reject"
            />
            <div class="column no-wrap q-gutter-y-sm" data-testid="flow-fallback">
              <div class="text-subtitle2">{{ t('approval.designer.basic.fallback') }}</div>
              <q-option-group
                style="overflow-wrap: anywhere" :model-value="fallbackMode" type="radio" role="radiogroup" :aria-label="t('approval.designer.basic.fallback')" data-testid="flow-fallback-mode"
                :options="[{ value: 'inherit', label: inheritText }, { value: 'custom', label: t('approval.designer.basic.fallbackCustom') }]"
                @update:model-value="setFallbackMode"
              />
              <ApproverSourceEditor
                v-if="form.fallback" :model-value="form.fallback" :module="module" :allowed="FALLBACK_SOURCE_KINDS" :limits="limits" fallback
                :saved="flow?.definition.fallbackApprovers" :label="t('approval.designer.basic.fallbackSource')" :error="fallbackError"
                @update:model-value="(v) => { form.fallback = v; }"
              />
              <div v-else-if="fallbackError" class="text-caption text-negative">{{ fallbackError }}</div>
            </div>
            <q-toggle v-if="!isEdit" v-model="form.active" :label="t('approval.designer.basic.active')" data-testid="flow-active" />
          </div>

          <div v-show="section === 'stages'" data-testid="flow-stages-section">
            <FlowStagesEditor
              v-model="form.stages" :module="module" :editable-field-ids="workflow.editableFieldIds ?? []" :limits="limits"
              :saved="flow?.definition.stages" :errors="errs" :disable="save.isPending.value"
            />
          </div>
        </div>
      </div>
      <q-separator />
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" data-testid="flow-cancel" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" unelevated :label="t('approval.designer.dialog.save')" :loading="save.isPending.value" data-testid="flow-save" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.flow-dialog--desktop { width: 960px; max-width: 95vw; height: 85vh; }
.flow-dialog__side { width: 140px; flex: none; }
</style>
