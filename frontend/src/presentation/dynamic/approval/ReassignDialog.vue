<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useReassignTask } from 'src/application/dynamic/queries/approval-queries';
import { useEffectivePolicies } from 'src/application/dynamic/queries/policies-queries';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import { currentStage } from './approval-actions';
import { targetCandidates } from './approval-targets';
import UserPickDialogBody from './UserPickDialogBody.vue';
import { useApprovalAction } from './use-approval-action';

const props = defineProps<{ modelValue: boolean; recordId: number; moduleKey: string; instance: ApprovalInstanceView }>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const reassign = useReassignTask();
const users = useUsers();
const policies = useEffectivePolicies(() => props.moduleKey);

// The manager picks which open task of the current stage to move; with only one it is preselected.
const openTasks = computed(() => (currentStage(props.instance)?.tasks ?? []).filter((k) => k.status === 'open'));
const taskOptions = computed(() => openTasks.value.map((k) => ({ value: String(k.id), label: k.assigneeName || k.assigneeUserId })));
const taskId = ref<string | null>(openTasks.value.length === 1 ? String(openTasks.value[0]!.id) : null);
const userId = ref<string | null>(null);
const comment = ref('');
const missingTask = ref(false);
const missingUser = ref(false);
watch(taskId, () => { missingTask.value = false; });
watch(userId, () => { missingUser.value = false; });

// The manager is not "me" here: handing a task to the manager is allowed (the server refuses the initiator and holders of the stage).
const candidates = computed(() => targetCandidates(
  users.data.value ?? [], props.instance, '', policies.data.value?.values['approval.allowSelfApproval'] === true,
));

const close = () => emit('update:modelValue', false);
const action = useApprovalAction({
  recordId: () => props.recordId, moduleKey: () => props.moduleKey, close, fieldKeys: () => ['userId'],
  send: (idempotencyKey) => reassign.mutateAsync({
    taskId: Number(taskId.value), recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey,
    body: { rowVersion: props.instance.rowVersion, userId: userId.value!, ...(comment.value.trim() && { comment: comment.value.trim() }) },
  }),
});
function send() {
  missingTask.value = !taskId.value;
  missingUser.value = !userId.value;
  if (!missingTask.value && !missingUser.value) void action.submit();
}
const userError = computed(() => (missingUser.value ? t('approval.actions.reassignToRequired') : action.fieldErrors.value.userId));
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw; max-height: 90vh'" data-testid="reassign-dialog">
      <q-card-section class="text-h6">{{ t('approval.actions.reassignTitle') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <div class="q-mb-md" data-testid="reassign-task">
          <GenericSelect
            v-model="taskId" :label="t('approval.actions.reassignTask')" :options="taskOptions" required
            :error="missingTask ? t('approval.actions.reassignTaskRequired') : undefined"
          />
        </div>
        <UserPickDialogBody
          v-model:user-id="userId" v-model:comment="comment" testid="reassign" :hint="t('approval.actions.reassignHint')"
          :user-label="t('approval.actions.reassignTo')" :comment-label="t('approval.actions.commentOptional')" :candidates="candidates"
          :user-error="userError" :comment-error="action.fieldErrors.value.comment" :max-comment="action.maxComment.value"
          :top-error="action.topError.value" :stale="action.stale.value" @reload="action.reload"
        />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" :disable="action.pending.value" @click="close" />
        <q-btn color="primary" :label="t('approval.actions.reassign')" :loading="action.pending.value" data-testid="reassign-send" @click="send" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
