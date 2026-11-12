<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRejectTask } from 'src/application/dynamic/queries/approval-queries';
import { useApprovalAction } from './use-approval-action';

const props = defineProps<{
  modelValue: boolean; recordId: number; moduleKey: string; taskId: number; rowVersion: string; canReturnPrevious: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const reject = useRejectTask();
const to = ref<'initiator' | 'previous'>('initiator');
const comment = ref('');
const missing = ref(false);

const close = () => emit('update:modelValue', false);
const action = useApprovalAction({
  recordId: () => props.recordId, moduleKey: () => props.moduleKey, close,
  send: (idempotencyKey) => reject.mutateAsync({
    taskId: props.taskId, recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey,
    body: { rowVersion: props.rowVersion, comment: comment.value.trim(), to: to.value },
  }),
});
// A required comment is checked here first; the server's own 400 still shows under the field.
function send() {
  missing.value = !comment.value.trim();
  if (!missing.value) void action.submit();
}
const commentError = computed(() => (missing.value ? t('approval.actions.commentRequired') : action.fieldErrors.value.comment));
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw; max-height: 90vh'" data-testid="reject-dialog">
      <q-card-section class="text-h6">{{ t('approval.actions.rejectTitle') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <div class="column no-wrap q-mb-md">
          <q-radio v-model="to" val="initiator" :label="t('approval.actions.toInitiator')" data-testid="reject-to-initiator" />
          <q-radio v-model="to" val="previous" :label="t('approval.actions.toPrevious')" :disable="!canReturnPrevious" data-testid="reject-to-previous" />
          <div v-if="!canReturnPrevious" class="text-caption text-muted q-pl-sm">{{ t('approval.actions.noPrevious') }}</div>
        </div>
        <q-input
          v-model="comment" type="textarea" outlined autogrow :label="t('approval.actions.commentRequiredLabel')" @update:model-value="missing = false" :maxlength="action.maxComment.value"
          :counter="action.maxComment.value !== undefined" :error="!!commentError" :error-message="commentError" data-testid="reject-comment"
        />
        <div v-if="action.topError.value" class="text-negative q-mt-md" data-testid="action-error">
          {{ action.topError.value }}
          <q-btn v-if="action.stale.value" flat dense color="primary" :label="t('dynamic.reload')" data-testid="action-reload" @click="action.reload" />
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" :disable="action.pending.value" @click="close" />
        <q-btn color="negative" :label="t('approval.actions.reject')" :loading="action.pending.value" data-testid="reject-send" @click="send" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
