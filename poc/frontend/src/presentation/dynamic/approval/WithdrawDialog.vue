<script setup lang="ts">
import { useQuasar } from 'quasar';
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useWithdrawApproval } from 'src/application/dynamic/queries/approval-queries';
import { useApprovalAction } from './use-approval-action';

const props = defineProps<{ modelValue: boolean; recordId: number; moduleKey: string; instanceId: number; rowVersion: string }>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const withdraw = useWithdrawApproval();
const comment = ref('');

const close = () => emit('update:modelValue', false);
const action = useApprovalAction({
  recordId: () => props.recordId, moduleKey: () => props.moduleKey, close,
  send: (idempotencyKey) => withdraw.mutateAsync({
    instanceId: props.instanceId, recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey,
    body: { rowVersion: props.rowVersion, ...(comment.value.trim() && { comment: comment.value.trim() }) },
  }),
});
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw; max-height: 90vh'" data-testid="withdraw-dialog">
      <q-card-section class="text-h6">{{ t('approval.actions.withdrawTitle') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <div class="q-mb-md">{{ t('approval.actions.withdrawHint') }}</div>
        <q-input
          v-model="comment" type="textarea" outlined autogrow :label="t('approval.actions.commentOptional')" :maxlength="action.maxComment.value"
          :counter="action.maxComment.value !== undefined" :error="!!action.fieldErrors.value.comment" :error-message="action.fieldErrors.value.comment" data-testid="withdraw-comment"
        />
        <div v-if="action.topError.value" class="text-negative q-mt-md" data-testid="action-error">
          {{ action.topError.value }}
          <q-btn v-if="action.stale.value" flat dense color="primary" :label="t('dynamic.reload')" data-testid="action-reload" @click="action.reload" />
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" :disable="action.pending.value" @click="close" />
        <q-btn color="primary" :label="t('approval.actions.withdraw')" :loading="action.pending.value" data-testid="withdraw-send" @click="action.submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
