<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useDeleteTask, useTask, useTaskComments, useUpdateTask } from 'src/application/dynamic/queries/tasks-queries';
import DateSelect from 'src/components/framework/DateSelect.vue';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { TaskPatch, TaskStatus } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import MentionInput from './MentionInput.vue';
import TaskTimeline from './TaskTimeline.vue';

const props = defineProps<{ taskId: number | null }>();
const emit = defineEmits<{ 'update:taskId': [value: number | null] }>();
const { t } = useI18n();
const router = useRouter();
const task = useTask(() => props.taskId);
const comments = useTaskComments(() => props.taskId);
const users = useUsers();
const update = useUpdateTask();
const del = useDeleteTask();

const data = computed(() => task.data.value);
const missing = computed(() => !!task.error.value && !data.value);
const draft = ref({ title: '', description: '', assignee: null as string | null, dueDate: null as string | null, status: 'todo' as TaskStatus });
const error = ref('');
const confirming = ref(false);

watch(data, (d) => {
  if (!d) return;
  draft.value = { title: d.title, description: d.description ?? '', assignee: d.assigneeUserId, dueDate: d.dueDate, status: d.status };
}, { immediate: true });

const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName })));
const statusOptions = computed(() => (['todo', 'inProgress', 'done', 'cancelled'] as const).map((s) => ({ value: s, label: t(`tasks.status.${s}`) })));

/** Only what changed goes out; an emptied due date / assignee / description is sent as an explicit null. */
const patch = computed<TaskPatch>(() => {
  const d = data.value;
  if (!d) return {};
  const p: TaskPatch = {};
  const dr = draft.value;
  if (dr.title.trim() !== d.title) p.title = dr.title.trim();
  if ((dr.description.trim() || null) !== d.description) p.description = dr.description.trim() || null;
  if (dr.assignee !== d.assigneeUserId) p.assigneeUserId = dr.assignee;
  if (dr.dueDate !== d.dueDate) p.dueDate = dr.dueDate;
  if (dr.status !== d.status) p.status = dr.status;
  return p;
});
const dirty = computed(() => Object.keys(patch.value).length > 0);
const readonly = computed(() => !data.value?.canEdit);

async function save() {
  if (!dirty.value || !draft.value.title.trim()) return;
  error.value = '';
  try {
    await update.mutateAsync({ id: props.taskId!, patch: patch.value });
  } catch (e) {
    const err = e as ApiError;
    error.value = (err.errors ? Object.values(err.errors).flat().join(t('common.listSeparator')) : err.message) || t('tasks.saveFailed');
  }
}

async function remove() {
  try {
    await del.mutateAsync(props.taskId!);
    emit('update:taskId', null);
  } catch (e) {
    error.value = (e as ApiError).message || t('tasks.saveFailed');
  } finally {
    confirming.value = false;
  }
}

const recordLink = computed(() => {
  const r = data.value?.record;
  return r?.accessible && !r.deleted && r.moduleKey ? `/dynamic/${r.moduleKey}/${r.recordId}` : null;
});
// The record can be visible while its title field is hidden by field permissions (the server then sends no title).
const recordTitle = computed(() => data.value?.record?.title || t('tasks.recordFallback', { id: data.value?.record?.recordId ?? '' }));

// Close first so the dialog's own router.replace starts before, and is cancelled by, the push to the record.
function goRecord() {
  emit('update:taskId', null);
  void router.push(recordLink.value!);
}
</script>

<template>
  <q-dialog
    :model-value="taskId !== null" :maximized="$q.screen.lt.sm" @update:model-value="(v) => { if (!v) emit('update:taskId', null); }"
  >
    <q-card style="width: 640px; max-width: 100vw" class="column no-wrap" data-testid="task-dialog">
      <template v-if="missing || !data">
        <q-card-section v-if="missing" class="text-negative" data-testid="task-not-found">
          {{ (task.error.value as ApiError | null)?.status === 404 ? t('tasks.notFound') : t('tasks.loadFailed') }}
        </q-card-section>
        <q-card-section v-else class="text-muted">
          <q-spinner size="sm" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('tasks.close')" v-close-popup data-testid="task-dialog-close" />
        </q-card-actions>
      </template>
      <template v-else-if="data">
        <q-card-section class="scroll col" style="max-height: 80vh">
          <div v-if="data.frozen" class="text-caption text-muted q-mb-sm" data-testid="task-frozen">{{ t('tasks.frozen') }}</div>
          <div v-if="data.fromRule" class="text-caption text-muted q-mb-xs">{{ t('tasks.fromRule') }}</div>
          <div v-if="data.ruleDeleted" class="text-caption text-warning q-mb-xs" data-testid="task-rule-deleted">{{ t('tasks.ruleDeleted') }}</div>
          <div class="q-gutter-y-sm">
            <q-input v-model="draft.title" :label="t('tasks.fields.title')" maxlength="200" outlined dense :readonly="readonly" data-testid="task-title" />
            <q-input v-model="draft.description" :label="t('tasks.fields.description')" type="textarea" autogrow maxlength="4000" outlined dense :readonly="readonly" data-testid="task-description" />
            <div class="row q-col-gutter-sm">
              <div class="col-12 col-sm-6">
                <GenericSelect v-model="draft.assignee" :label="t('tasks.fields.assignee')" :options="userOptions" :readonly="readonly" @update:model-value="(v) => (draft.assignee = v as string | null)" />
              </div>
              <div class="col-12 col-sm-6">
                <DateSelect v-model="draft.dueDate" :label="t('tasks.fields.dueDate')" :readonly="readonly" />
              </div>
              <div class="col-12 col-sm-6">
                <GenericSelect v-model="draft.status" :label="t('tasks.fields.status')" :options="statusOptions" :readonly="readonly" required @update:model-value="(v) => (draft.status = v as TaskStatus)" />
              </div>
            </div>
            <div data-testid="task-record" class="text-body2">
              <template v-if="!data.record"><span class="text-caption text-muted">{{ t('tasks.fields.record') }}{{ t('tasks.colon') }}</span>{{ t('tasks.none') }}</template>
              <template v-else-if="recordLink"><span class="text-caption text-muted">{{ t('tasks.fields.record') }}{{ t('tasks.colon') }}</span><router-link :to="recordLink" @click.prevent="goRecord">{{ recordTitle }}</router-link></template>
              <span v-else-if="data.record.deleted" class="text-muted">{{ t('tasks.recordDeleted', { title: recordTitle }) }}</span>
              <span v-else class="text-muted">{{ t('tasks.recordNoAccess') }}</span>
            </div>
            <div v-if="data.createdByName" class="text-caption text-muted">{{ t('tasks.createdBy') }}{{ t('tasks.colon') }}{{ data.createdByName }}</div>
            <div v-if="error" class="text-negative text-caption" data-testid="task-error">{{ error }}</div>
          </div>
          <q-separator class="q-my-md" />
          <div class="text-subtitle2">{{ t('tasks.comment') }}</div>
          <TaskTimeline :task-id="data.id" :comments="comments.data.value ?? []" :readonly="!data.canComment" />
          <MentionInput v-if="data.canComment" :task-id="data.id" />
        </q-card-section>
        <q-separator />
        <q-card-actions align="right">
          <q-btn v-if="data.canDelete" flat color="negative" no-caps :label="t('tasks.delete')" data-testid="task-delete" @click="confirming = true" />
          <q-space />
          <q-btn flat no-caps :label="t('tasks.close')" v-close-popup />
          <q-btn v-if="data.canEdit" color="primary" no-caps :label="t('tasks.save')" :disable="!dirty" :loading="update.isPending.value" data-testid="task-save" @click="save" />
        </q-card-actions>
      </template>
      <q-dialog v-model="confirming">
        <q-card>
          <q-card-section>{{ t('tasks.confirmDelete') }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('tasks.cancel')" v-close-popup />
            <q-btn color="negative" :label="t('tasks.delete')" :loading="del.isPending.value" data-testid="task-delete-confirm" @click="remove" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </q-card>
  </q-dialog>
</template>
