<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useSaveApprovalFlow } from 'src/application/dynamic/queries/approval-flow-queries';
import {
  buildFlowInput, definitionChanged, draftFromFlow, emptyFlowDraft, nextPriority, type FlowDraft,
} from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow, SequenceFlow } from 'src/domain/dynamic/approval-flow-types';
import type { ModuleDetail, WorkflowResponse } from 'src/domain/dynamic/types';
import { errorFor } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import FlowBasicFields, { type FlowBasic } from './FlowBasicFields.vue';
import FlowStagesEditor from './FlowStagesEditor.vue';
import FlowTrialPanel from './FlowTrialPanel.vue';
import { useApprovalLimits } from './use-approval-limits';

const props = defineProps<{
  modelValue: boolean;
  module: ModuleDetail;
  workflow: WorkflowResponse;
  flows: ApprovalFlow[];
  flow?: SequenceFlow;
  presetTarget?: number | null;
}>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean]; saved: [flow: ApprovalFlow | undefined] }>();
const { t } = useI18n();
const $q = useQuasar();
const mobile = computed(() => $q.screen.lt.sm);

const isEdit = computed(() => !!props.flow);
const section = ref<'basic' | 'stages' | 'trial'>('basic');
const form = ref<FlowDraft>(emptyFlowDraft(null, 1));
const error = ref<ApiError | null>(null);
const targetMissing = ref(false);
const basicFields = ref<{ validate(): Promise<boolean> } | null>(null);

watch(() => [props.modelValue, props.flow?.key, props.presetTarget] as const, ([open]) => {
  if (!open) return;
  section.value = 'basic';
  targetMissing.value = false;
  const f = props.flow;
  const preset = props.presetTarget ?? null;
  form.value = f ? draftFromFlow(f) : emptyFlowDraft(preset, preset === null ? 1 : nextPriority(props.flows, preset));
  error.value = null;
}, { immediate: true });
// Server errors point at indexes and values that an edit moves, so any edit clears them (as the rules tab does).
watch(form, () => { error.value = null; }, { deep: true });

const { limits } = useApprovalLimits(() => props.module.key);

const targetLocked = computed(() => !!props.flow?.used);

function setTarget(id: number) {
  form.value.toOptionId = id;
  targetMissing.value = false;
  // Same target as saved keeps its place; a new target goes to the end of that target's list.
  form.value.priority = props.flow && props.flow.toOptionId === id ? props.flow.priority : nextPriority(props.flows, id, props.flow?.key);
}
function onBasicPatch(p: Partial<FlowBasic>) {
  const { toOptionId, ...rest } = p;
  Object.assign(form.value, rest);
  if (toOptionId !== undefined && toOptionId !== null) setTarget(toOptionId);
}

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
const errs = computed(() => error.value?.errors);
const stageKey = /^definition\.stages\[\d+\]/;
const own = /^(key|name|toOptionId|priority|definition\.(when|rejectTo|fallbackApprovers))(\.|\[|$)/;
const keyError = computed(() => errorFor(error.value, 'key') ?? (error.value?.status === 409 && !isEdit.value && !Object.keys(errs.value ?? {}).length ? error.value.message : undefined));
const nameError = computed(() => errorFor(error.value, 'name'));
const targetError = computed(() => (targetMissing.value ? t('approval.designer.basic.targetRequired') : errorFor(error.value, 'toOptionId')));
const basicErrors = computed(() => ({ key: keyError.value, name: nameError.value, toOptionId: targetError.value, priority: priorityError.value, when: whenError.value, rejectTo: rejectError.value, fallback: fallbackError.value }));
const whenError = computed(() => errorFor(error.value, 'definition.when'));
const rejectError = computed(() => errorFor(error.value, 'definition.rejectTo'));
const fallbackError = computed(() => errorFor(error.value, 'definition.fallbackApprovers'));
const priorityError = computed(() => errorFor(error.value, 'priority'));
const topErrors = computed(() => Object.entries(errs.value ?? {}).filter(([k]) => !own.test(k) && !stageKey.test(k)).flatMap(([, v]) => v));
const stageErrorCount = computed(() => Object.keys(errs.value ?? {}).filter((k) => stageKey.test(k)).length);

async function submit() {
  if (!(await basicFields.value?.validate())) { section.value = 'basic'; return; }
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
          <q-tab name="trial" :label="t('approval.designer.dialog.sectionTrial')" data-testid="section-trial" />
        </q-tabs>
        <q-separator :vertical="!mobile" />
        <div class="col scroll q-pa-md" style="min-width: 0">
          <q-banner v-if="banner" dense rounded class="bg-info-soft q-mb-md" data-testid="flow-banner">{{ banner }}</q-banner>
          <div v-if="topErrors.length" class="text-negative q-mb-md" data-testid="flow-top-errors">
            <div v-for="(m, i) in topErrors" :key="i">{{ m }}</div>
          </div>

          <div v-show="section === 'basic'" class="column no-wrap q-gutter-y-md" data-testid="flow-basic">
            <FlowBasicFields
              ref="basicFields" :model-value="form" :module="module" :workflow="workflow" :key-readonly="isEdit" :target-locked="targetLocked"
              :saved-fallback="flow?.definition.fallbackApprovers" :errors="basicErrors" @patch="onBasicPatch"
            />
            <q-toggle v-if="!isEdit" v-model="form.active" :label="t('approval.designer.basic.active')" data-testid="flow-active" />
          </div>

          <div v-show="section === 'stages'" data-testid="flow-stages-section">
            <FlowStagesEditor
              v-model="form.stages" :module="module" :editable-field-ids="workflow.editableFieldIds ?? []" :limits="limits"
              :saved="flow?.definition.stages" :errors="errs" :disable="save.isPending.value"
            />
          </div>

          <div v-show="section === 'trial'" data-testid="flow-trial-section">
            <FlowTrialPanel :module="module" :draft="form" :flows="flows" />
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
