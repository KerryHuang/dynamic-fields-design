<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { usePreviewApprovalDraft } from 'src/application/dynamic/queries/approval-flow-queries';
import { buildFlowInput, type FlowDraft } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { ApprovalPreview } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import PreviewStages from 'src/presentation/dynamic/approval/PreviewStages.vue';
import type { ApiError } from 'src/services/api/http';
import { trialFieldIds } from './trial-fields';
import TrialInputs from './TrialInputs.vue';

const props = defineProps<{ module: ModuleDetail; draft: FlowDraft; flows: ApprovalFlow[] }>();
const { t, locale } = useI18n();

// What the trial sends: the draft as it is being edited, always as active (the backend treats it so too).
const input = computed(() => buildFlowInput({ ...props.draft, active: true }));

const fieldIds = computed(() => (input.value ? trialFieldIds(props.flows, input.value) : []));
const inputs = ref<InstanceType<typeof TrialInputs> | null>(null);
const initiator = computed(() => inputs.value?.initiator ?? null);

// ---- run ----
const preview = usePreviewApprovalDraft(() => props.module.key);
const result = ref<ApprovalPreview | null>(null);
const errors = ref<string[]>([]);
const failed = ref(false);
// An edit makes the shown result stale; a response that arrives after an edit (or a newer run) is dropped.
let runId = 0;
const reset = () => { runId++; result.value = null; errors.value = []; failed.value = false; };
watch(() => JSON.stringify(input.value), reset);

const stageErr = /^definition\.stages\[(\d+)\]/;
/** Messages with the stage they belong to (the keys are indexes into the draft's stages). */
const errorLines = (e: Record<string, string[]>) => Object.entries(e).flatMap(([k, v]) => {
  const n = stageErr.exec(k)?.[1];
  return v.map((m) => (n === undefined ? m : t('approval.designer.trial.stageError', { s: t('approval.designer.stage.title', { n: Number(n) + 1 }), m })));
});

function run() {
  if (!input.value || !initiator.value) return;
  result.value = null; errors.value = []; failed.value = false;
  const myRun = ++runId;
  preview.mutate({
    draft: input.value, initiatorUserId: initiator.value, recordId: inputs.value?.recordId ?? undefined, values: inputs.value?.sentValues,
  }, {
    onSuccess: (r) => { if (myRun === runId) result.value = r; },
    onError: (e) => {
      if (myRun !== runId) return;
      const err = e as unknown as ApiError;
      const msgs = errorLines(err.errors ?? {});
      if (err.status === 400 && msgs.length) errors.value = msgs;
      else failed.value = true;
    },
  });
}

const mine = computed(() => !!result.value && result.value.flowKey === input.value?.key);
const otherName = computed(() => {
  const r = result.value;
  if (!r || mine.value) return '';
  const saved = props.flows.find((f) => f.key === r.flowKey);
  return resolveI18n(saved?.name ?? r.flowName ?? {}, locale.value) || r.flowKey || '';
});
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="flow-trial">
    <div v-if="!input" class="text-muted" data-testid="trial-need-target">{{ t('approval.designer.trial.needTarget') }}</div>
    <template v-else>
      <TrialInputs ref="inputs" :module="module" :field-ids="fieldIds" @change="reset" />
      <div>
        <q-btn
          color="primary" unelevated no-caps :label="t('approval.designer.trial.run')" :disable="!initiator" :loading="preview.isPending.value"
          data-testid="trial-run" @click="run"
        />
      </div>

      <div v-if="errors.length" class="text-negative" data-testid="trial-errors">
        <div class="text-subtitle2">{{ t('approval.designer.trial.draftInvalid') }}</div>
        <div v-for="(m, i) in errors" :key="i">{{ m }}</div>
      </div>
      <div v-else-if="failed" class="text-negative" data-testid="trial-failed">{{ t('approval.designer.trial.failed') }}</div>
      <div v-else-if="result" data-testid="trial-result">
        <div v-if="result.outcome === 'blocked'" class="text-negative" data-testid="trial-blocked">{{ t('approval.designer.trial.blocked') }}</div>
        <div v-else-if="result.outcome === 'direct'" data-testid="trial-direct">{{ t('approval.designer.trial.direct') }}</div>
        <template v-else>
          <div v-if="mine" class="text-subtitle2 q-mb-sm" data-testid="trial-flow">{{ t('approval.designer.trial.matched', { name: resolveI18n(result.flowName ?? {}, locale) }) }}</div>
          <q-banner v-else dense rounded class="bg-info-soft q-mb-sm" data-testid="trial-other-flow">{{ t('approval.designer.trial.otherFlow', { name: otherName }) }}</q-banner>
          <PreviewStages :stages="result.stages" designer />
        </template>
      </div>
      <div class="text-caption text-muted">{{ t('approval.designer.trial.note') }}</div>
    </template>
  </div>
</template>
