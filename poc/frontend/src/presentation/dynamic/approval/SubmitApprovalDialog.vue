<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useApprovalPreview, useSubmitApproval } from 'src/application/dynamic/queries/approval-queries';
import { useEffectivePolicies } from 'src/application/dynamic/queries/policies-queries';
import type { PickRequirement, SubmitApprovalResult } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { joinList } from 'src/i18n/translate';
import { isStale } from 'src/presentation/dynamic/designer/api-error';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import type { ApiError } from 'src/services/api/http';
import PreviewStages from './PreviewStages.vue';

const props = defineProps<{
  modelValue: boolean; recordId: number; moduleKey: string; to: number; toLabel: string; recordRowVersion: string;
}>();
const emit = defineEmits<{ 'update:modelValue': [boolean]; submitted: [SubmitApprovalResult['outcome']] }>();

const { t, locale } = useI18n();
const $q = useQuasar();
const qc = useQueryClient();
const submit = useSubmitApproval();
const preview = useApprovalPreview(() => props.recordId, () => props.to, () => props.modelValue);
const policies = useEffectivePolicies(() => props.moduleKey);

// The limit comes from the policy; without a value no maxlength is set and the server's 400 is the only guard.
const maxComment = computed(() => {
  const v = policies.data.value?.values['approval.maxCommentLength'];
  return typeof v === 'number' ? v : undefined;
});

const comment = ref('');
const picks = reactive<Record<string, string[]>>({});
const topError = ref('');
const fieldErrors = ref<Record<string, string>>({});
// One key per opening: a resend after a network error reuses it (the server replays the first result); a success or a 422 replaces it.
let idempotencyKey = newIdempotencyKey();
watch(() => props.modelValue, (open) => {
  if (!open) return;
  idempotencyKey = newIdempotencyKey();
  comment.value = '';
  for (const k of Object.keys(picks)) delete picks[k];
  topError.value = '';
  fieldErrors.value = {};
});

const data = computed(() => preview.data.value);
const blocked = computed(() => data.value?.outcome === 'blocked');
const direct = computed(() => data.value?.outcome === 'direct');
const countOk = (p: PickRequirement) => (picks[p.stageCode]?.length ?? 0) >= p.min && (picks[p.stageCode]?.length ?? 0) <= p.max;
const canSubmit = computed(() => !!data.value && !blocked.value && (data.value.picks ?? []).every(countOk) && !submit.isPending.value);
const options = (p: PickRequirement) => p.candidates.map((c) => ({ label: c.displayName, value: c.userId }));
const pickHint = (p: PickRequirement) => (p.min === p.max ? t('approval.submit.pickExact', { n: p.min }) : t('approval.submit.pickRange', { min: p.min, max: p.max }));
const loadError = computed(() => (preview.error.value as ApiError | null)?.message ?? (preview.error.value ? t('approval.submit.previewFailed') : ''));

const close = () => emit('update:modelValue', false);
const refetchRecord = () => Promise.all([
  qc.invalidateQueries({ queryKey: ['record', props.recordId] }),
  qc.invalidateQueries({ queryKey: ['record-approval', props.recordId] }),
]);

async function onSubmit() {
  topError.value = '';
  fieldErrors.value = {};
  const required = data.value?.picks ?? [];
  try {
    const result = await submit.mutateAsync({
      recordId: props.recordId, idempotencyKey,
      body: {
        to: props.to, recordRowVersion: props.recordRowVersion,
        ...(required.length && { picks: Object.fromEntries(required.map((p) => [p.stageCode, picks[p.stageCode] ?? []])) }),
        ...(comment.value.trim() && { comment: comment.value.trim() }),
      },
    });
    idempotencyKey = newIdempotencyKey();
    emit('submitted', result.outcome);
    close();
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 422) idempotencyKey = newIdempotencyKey(); // the key was used for other content
    if (err.status === 400 && err.errors) {
      const rest: string[] = [];
      for (const [k, v] of Object.entries(err.errors)) {
        if (k === 'comment' || k.startsWith('picks.')) fieldErrors.value[k] = joinList(v);
        else rest.push(...v);
      }
      topError.value = rest.join('；');
    } else if (err.status === 409 && isStale(err)) {
      topError.value = t('dynamic.stale');
      void refetchRecord();
    } else if (err.status === 409) {
      // Someone else already started or finished it: the server message says what, the page shows the new state.
      $q.notify({ type: 'warning', message: err.message ?? t('dynamic.loadFailed') });
      void refetchRecord();
      close();
    } else topError.value = err.message ?? t('dynamic.loadFailed');
  }
}
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 560px; max-width: 95vw; max-height: 90vh'" data-testid="submit-approval-dialog">
      <q-card-section class="text-h6">{{ t('approval.submit.title', { state: toLabel }) }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <div v-if="preview.isPending.value" class="row justify-center q-pa-md"><q-spinner color="primary" size="2em" /></div>
        <div v-else-if="loadError" class="text-negative" data-testid="preview-error">{{ loadError }}</div>
        <template v-else-if="data">
          <div v-if="blocked" class="text-negative q-mb-md" data-testid="preview-blocked">{{ t('approval.submit.blocked') }}</div>
          <div v-else-if="direct" class="q-mb-md" data-testid="preview-direct">{{ t('approval.submit.direct', { state: toLabel }) }}</div>
          <template v-else>
            <div v-if="data.flowName" class="text-subtitle2 q-mb-sm" data-testid="preview-flow">{{ t('approval.submit.flow', { name: resolveI18n(data.flowName, locale) }) }}</div>
            <PreviewStages :stages="data.stages" />
            <div class="text-caption text-muted q-mb-md">{{ t('approval.submit.orgNote') }}</div>
            <div v-for="p in data.picks" :key="p.stageCode" :data-testid="`pick-${p.stageCode}`">
              <q-select
                v-model="picks[p.stageCode]" multiple use-chips emit-value map-options outlined dense
                :options="options(p)" :label="resolveI18n(p.name, locale)" :hint="pickHint(p)" class="q-mb-md"
                :error="!!fieldErrors[`picks.${p.stageCode}`]" :error-message="fieldErrors[`picks.${p.stageCode}`]"
              />
            </div>
          </template>
          <q-input
            v-if="!blocked" v-model="comment" type="textarea" outlined autogrow :label="t('approval.submit.comment')"
            :maxlength="maxComment" :counter="maxComment !== undefined" :error="!!fieldErrors.comment" :error-message="fieldErrors.comment" data-testid="submit-comment"
          />
        </template>
        <div v-if="topError" class="text-negative q-mt-md" data-testid="submit-error">{{ topError }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" @click="close" />
        <q-btn color="primary" :label="t('approval.submit.send')" :disable="!canSubmit" :loading="submit.isPending.value" data-testid="submit-approval-send" @click="onSubmit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
