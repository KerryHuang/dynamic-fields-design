<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useTransferTask } from 'src/application/dynamic/queries/approval-queries';
import { useEffectivePolicies } from 'src/application/dynamic/queries/policies-queries';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { targetCandidates } from './approval-targets';
import UserPickDialogBody from './UserPickDialogBody.vue';
import { useApprovalAction } from './use-approval-action';

const props = defineProps<{ modelValue: boolean; recordId: number; moduleKey: string; taskId: number; instance: ApprovalInstanceView }>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();
const transfer = useTransferTask();
const users = useUsers();
const policies = useEffectivePolicies(() => props.moduleKey);
const userId = ref<string | null>(null);
const comment = ref('');
const missing = ref(false);
watch(userId, () => { missing.value = false; });

// Without a policy value the initiator stays out of the list; the server decides either way.
const candidates = computed(() => targetCandidates(
  users.data.value ?? [], props.instance, identity.userId, policies.data.value?.values['approval.allowSelfApproval'] === true,
));

const close = () => emit('update:modelValue', false);
const action = useApprovalAction({
  recordId: () => props.recordId, moduleKey: () => props.moduleKey, close, fieldKeys: () => ['userId'],
  send: (idempotencyKey) => transfer.mutateAsync({
    taskId: props.taskId, recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey,
    body: { rowVersion: props.instance.rowVersion, userId: userId.value!, ...(comment.value.trim() && { comment: comment.value.trim() }) },
  }),
});
function send() {
  missing.value = !userId.value;
  if (!missing.value) void action.submit();
}
const userError = computed(() => (missing.value ? t('approval.actions.transferToRequired') : action.fieldErrors.value.userId));
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw; max-height: 90vh'" data-testid="transfer-dialog">
      <q-card-section class="text-h6">{{ t('approval.actions.transferTitle') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <UserPickDialogBody
          v-model:user-id="userId" v-model:comment="comment" testid="transfer" :hint="t('approval.actions.transferHint')"
          :user-label="t('approval.actions.transferTo')" :comment-label="t('approval.actions.commentOptional')" :candidates="candidates"
          :user-error="userError" :comment-error="action.fieldErrors.value.comment" :max-comment="action.maxComment.value"
          :top-error="action.topError.value" :stale="action.stale.value" @reload="action.reload"
        />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" :disable="action.pending.value" @click="close" />
        <q-btn color="primary" :label="t('approval.actions.transfer')" :loading="action.pending.value" data-testid="transfer-send" @click="send" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
