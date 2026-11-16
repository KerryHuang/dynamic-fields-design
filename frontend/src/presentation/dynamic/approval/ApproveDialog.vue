<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAddSignTask, useApproveTask } from 'src/application/dynamic/queries/approval-queries';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useEffectivePolicies } from 'src/application/dynamic/queries/policies-queries';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import type { DynamicRecord, ModuleSchema } from 'src/domain/dynamic/types';
import { useIdentityStore } from 'src/stores/use-identity-store';
import ApprovalEditableFields from './ApprovalEditableFields.vue';
import { targetCandidates } from './approval-targets';
import { useApprovalAction } from './use-approval-action';

const props = defineProps<{
  modelValue: boolean; recordId: number; moduleKey: string; taskId: number; rowVersion: string;
  /** The current stage's editable field keys; the section shows only when the schema and the record are at hand. */
  editableFieldKeys?: string[]; schema?: ModuleSchema | null; record?: DynamicRecord;
  /** Approve and add a stage: the dialog also asks for the people to add and the mode; needs `instance` for the candidate list. */
  addSign?: boolean; instance?: ApprovalInstanceView;
}>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const approve = useApproveTask();
const addSignTask = useAddSignTask();
const identity = useIdentityStore();
const users = useUsers({ enabled: () => !!props.addSign });
const comment = ref('');
const addUsers = ref<string[]>([]);
const addMode = ref<'all' | 'any'>('any');
const missingUsers = ref(false);
const editable = ref<InstanceType<typeof ApprovalEditableFields> | null>(null);
let changed: Record<string, unknown> = {};

const keys = computed(() => props.editableFieldKeys ?? []);
const hasEditable = computed(() => keys.value.length > 0 && !!props.schema && !!props.record
  && props.schema.blocks.some((b) => b.fields.some((f) => keys.value.includes(f.key))));

const close = () => emit('update:modelValue', false);
const common = () => ({
  rowVersion: props.rowVersion, ...(comment.value.trim() && { comment: comment.value.trim() }),
  ...(Object.keys(changed).length > 0 && { values: changed }),
});
const action = useApprovalAction({
  recordId: () => props.recordId, moduleKey: () => props.moduleKey, close, fieldKeys: () => [...(editable.value?.drawn ?? []), ...(props.addSign ? ['userIds'] : [])],
  send: (idempotencyKey) => props.addSign
    ? addSignTask.mutateAsync({
      taskId: props.taskId, recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey,
      body: { ...common(), userIds: addUsers.value, mode: addMode.value },
    })
    : approve.mutateAsync({ taskId: props.taskId, recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey, body: common() }),
});

// The cap comes from the policy; without a value nothing is capped here and the server's 400 is the guard (same as the comment length).
const policies = useEffectivePolicies(() => props.moduleKey);
const maxUsers = computed(() => {
  const v = policies.data.value?.values['approval.maxAddSignUsers'];
  return typeof v === 'number' ? v : undefined;
});
const atCap = computed(() => maxUsers.value !== undefined && addUsers.value.length >= maxUsers.value);
const candidates = computed(() => (props.instance ? targetCandidates(
  users.data.value ?? [], props.instance, identity.userId, policies.data.value?.values['approval.allowSelfApproval'] === true, true,
) : []).map((o) => ({ ...o, disable: atCap.value && !addUsers.value.includes(o.value) })));
const modeOptions = computed(() => (['any', 'all'] as const).map((m) => ({ value: m, label: t(`approval.mode.${m}`) })));
const usersError = computed(() => (missingUsers.value ? t('approval.actions.addSignUsersRequired') : action.fieldErrors.value.userIds));
watch(addUsers, () => { missingUsers.value = false; });

async function send() {
  missingUsers.value = !!props.addSign && addUsers.value.length === 0;
  if (missingUsers.value) return;
  changed = {};
  if (editable.value) {
    const v = await editable.value.collect();
    if (!v) return;
    changed = v;
  }
  await action.submit();
}
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw; max-height: 90vh'" data-testid="approve-dialog">
      <q-card-section class="text-h6">{{ t(addSign ? 'approval.actions.addSignTitle' : 'approval.actions.approveTitle') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <template v-if="addSign">
          <div class="q-mb-md approval-wrap">{{ t('approval.actions.addSignHint') }}</div>
          <div class="q-mb-md" data-testid="add-sign-users">
            <GenericSelect v-model="addUsers" :label="t('approval.actions.addSignUsers')" :options="candidates" :error="usersError" multiple required />
            <div v-if="maxUsers !== undefined" class="text-caption text-muted" data-testid="add-sign-cap">{{ t('approval.actions.addSignCap', { max: maxUsers }) }}</div>
          </div>
          <div class="q-mb-md" data-testid="add-sign-mode">
            <div class="text-caption text-muted">{{ t('approval.actions.addSignMode') }}</div>
            <q-option-group v-model="addMode" :options="modeOptions" type="radio" inline :aria-label="t('approval.actions.addSignMode')" />
          </div>
        </template>
        <ApprovalEditableFields
          v-if="hasEditable" ref="editable" :keys="keys" :schema="schema!" :record="record!" :errors="action.fieldErrors.value"
        />
        <q-input
          v-model="comment" type="textarea" outlined autogrow :label="t('approval.actions.commentOptional')" :maxlength="action.maxComment.value"
          :counter="action.maxComment.value !== undefined" :error="!!action.fieldErrors.value.comment" :error-message="action.fieldErrors.value.comment" data-testid="approve-comment"
        />
        <div v-if="action.topError.value" class="text-negative q-mt-md" data-testid="action-error">
          {{ action.topError.value }}
          <q-btn v-if="action.stale.value" flat dense color="primary" :label="t('dynamic.reload')" data-testid="action-reload" @click="action.reload" />
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" :disable="action.pending.value" @click="close" />
        <q-btn
          color="primary" :label="t(addSign ? 'approval.actions.addSign' : 'approval.actions.approve')" :loading="action.pending.value"
          data-testid="approve-send" @click="send"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.approval-wrap { overflow-wrap: anywhere; }
</style>
