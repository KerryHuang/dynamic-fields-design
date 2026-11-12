<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useApproveTask } from 'src/application/dynamic/queries/approval-queries';
import type { DynamicRecord, ModuleSchema } from 'src/domain/dynamic/types';
import ApprovalEditableFields from './ApprovalEditableFields.vue';
import { useApprovalAction } from './use-approval-action';

const props = defineProps<{
  modelValue: boolean; recordId: number; moduleKey: string; taskId: number; rowVersion: string;
  /** The current stage's editable field keys; the section shows only when the schema and the record are at hand. */
  editableFieldKeys?: string[]; schema?: ModuleSchema | null; record?: DynamicRecord;
}>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const approve = useApproveTask();
const comment = ref('');
const editable = ref<InstanceType<typeof ApprovalEditableFields> | null>(null);
let changed: Record<string, unknown> = {};

const keys = computed(() => props.editableFieldKeys ?? []);
const hasEditable = computed(() => keys.value.length > 0 && !!props.schema && !!props.record
  && props.schema.blocks.some((b) => b.fields.some((f) => keys.value.includes(f.key))));

const close = () => emit('update:modelValue', false);
const action = useApprovalAction({
  recordId: () => props.recordId, moduleKey: () => props.moduleKey, close, fieldKeys: () => keys.value,
  send: (idempotencyKey) => approve.mutateAsync({
    taskId: props.taskId, recordId: props.recordId, moduleKey: props.moduleKey, idempotencyKey,
    body: {
      rowVersion: props.rowVersion, ...(comment.value.trim() && { comment: comment.value.trim() }),
      ...(Object.keys(changed).length > 0 && { values: changed }),
    },
  }),
});

async function send() {
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
      <q-card-section class="text-h6">{{ t('approval.actions.approveTitle') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
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
        <q-btn color="primary" :label="t('approval.actions.approve')" :loading="action.pending.value" data-testid="approve-send" @click="send" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
