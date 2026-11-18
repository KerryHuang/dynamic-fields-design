<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useDeleteTask, useTask, useTaskComments, useUpdateTask } from 'src/application/dynamic/queries/tasks-queries';
import DateSelect from 'src/components/framework/DateSelect.vue';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import {
  isParentCancelled, isTaskHidden, startAfterDue, subtasksOpenCount, type TaskCascade, type TaskPatch, type TaskStatus,
} from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import MentionInput from './MentionInput.vue';
import SubtaskSection from './SubtaskSection.vue';
import SubtasksOpenDialog from './SubtasksOpenDialog.vue';
import TaskTimeline from './TaskTimeline.vue';
import { useMovedOutNotice } from './use-moved-out-notice';

/** unavailableText：讀不到任務（403、404）時顯示的文字，沒給就用預設訊息；報表的下鑽用它說「這筆任務已無法查看」。 */
const props = defineProps<{ taskId: number | null; unavailableText?: string }>();
const emit = defineEmits<{ 'update:taskId': [value: number | null] }>();
const { t } = useI18n();
const router = useRouter();
const task = useTask(() => props.taskId);
const comments = useTaskComments(() => props.taskId);
const users = useUsers();
const update = useUpdateTask();
const del = useDeleteTask();
const movedOut = useMovedOutNotice();

const data = computed(() => task.data.value);
const missing = computed(() => !!task.error.value && !data.value);
const draft = ref({
  title: '', description: '', assignee: null as string | null, startDate: null as string | null, dueDate: null as string | null, status: 'todo' as TaskStatus,
});
const error = ref('');
const confirming = ref(false);
// 父任務改完成、還有未完成子任務：後端回 409 subtasksOpen，三選一後帶 cascade 重送
const openCount = ref(0);
const openChoice = ref(false);

const draftOf = (d: NonNullable<typeof data.value>) => ({
  title: d.title, description: d.description ?? '', assignee: d.assigneeUserId, startDate: d.startDate, dueDate: d.dueDate, status: d.status,
});
// 同一個任務重抓（例如新增子任務後）只同步沒被使用者改過的欄位，改到一半的內容不能被蓋掉；換成別的任務才整份重新初始化
watch(data, (d, prev) => {
  if (!d) return;
  const next = draftOf(d);
  if (!prev || prev.id !== d.id) {
    draft.value = next;
    return;
  }
  const before = draftOf(prev);
  const cur = draft.value as Record<string, unknown>;
  for (const k of Object.keys(next) as (keyof typeof next)[]) {
    if (cur[k] === before[k]) cur[k] = next[k];
  }
}, { immediate: true });

const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName })));
const statusOptions = computed(() => (['todo', 'inProgress', 'done', 'cancelled'] as const).map((s) => ({ value: s, label: t(`tasks.status.${s}`) })));

/** Only what changed goes out; an emptied due date / assignee / description is sent as an explicit null. */
const patch = computed<TaskPatch>(() => {
  const d = data.value;
  if (!d) return {};
  const p: TaskPatch = {};
  const dr = draft.value;
  // A manager (no normal edit right) may only change the assignee; everything else is read-only and never sent
  if (!d.canEdit) {
    if (dr.assignee !== d.assigneeUserId) p.assigneeUserId = dr.assignee;
    return p;
  }
  if (dr.title.trim() !== d.title) p.title = dr.title.trim();
  if ((dr.description.trim() || null) !== d.description) p.description = dr.description.trim() || null;
  if (dr.assignee !== d.assigneeUserId) p.assigneeUserId = dr.assignee;
  if (dr.dueDate !== d.dueDate) p.dueDate = dr.dueDate;
  if (dr.startDate !== d.startDate) p.startDate = dr.startDate;
  if (dr.status !== d.status) p.status = dr.status;
  return p;
});
const dirty = computed(() => Object.keys(patch.value).length > 0);
// 送了開始日就看寫入後的整對值，矛盾時伺服器回 400；只改到期日、早於開始日時伺服器把開始日推到新到期日（GT-Q1），儲存前先提示
const startServerError = ref('');
watch(() => [draft.value.startDate, draft.value.dueDate], () => { startServerError.value = ''; });
const startError = computed(() => {
  if (patch.value.startDate === undefined) return startServerError.value;
  return startAfterDue(draft.value.startDate, draft.value.dueDate) ? t('tasks.startAfterDue') : startServerError.value;
});
const startFollows = computed(() => patch.value.startDate === undefined && patch.value.dueDate !== undefined
  && startAfterDue(draft.value.startDate, draft.value.dueDate));
const readonly = computed(() => !data.value?.canEdit);
/** The assignee stays open to a manager of a subordinate's task (canReassign) even when the rest is read-only. */
const assigneeReadonly = computed(() => !data.value?.canEdit && !data.value?.canReassign);

function failureText(e: unknown): string {
  const err = e as ApiError;
  if (isParentCancelled(err)) return t('tasks.subtask.parentCancelled');
  // 開始日的伺服器訊息掛在欄位下，不重複顯示在底下
  const { startDate: startErrors, ...others } = err.errors ?? {};
  startServerError.value = startErrors?.[0] ?? '';
  const text = err.errors ? Object.values(others).flat().join(t('common.listSeparator')) : err.message;
  return text || (startErrors ? '' : t('tasks.saveFailed'));
}

async function save(cascade?: TaskCascade) {
  if (!dirty.value || !draft.value.title.trim() || startError.value) return;
  error.value = '';
  try {
    const id = props.taskId!;
    const result = await update.mutateAsync({ id, patch: cascade ? { ...patch.value, cascade } : patch.value });
    openChoice.value = false;
    // The writer lost sight of the task (a manager handed it outside their scope): close and offer the undo
    if (isTaskHidden(result)) {
      emit('update:taskId', null);
      movedOut.show(result.id, result.undoToken);
    }
  } catch (e) {
    const count = subtasksOpenCount(e);
    if (count !== null && !cascade) {
      openCount.value = count;
      openChoice.value = true;
      return;
    }
    openChoice.value = false;
    error.value = failureText(e);
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
          {{ unavailableText && [403, 404].includes((task.error.value as ApiError | null)?.status ?? 0) ? unavailableText : (task.error.value as ApiError | null)?.status === 404 ? t('tasks.notFound') : t('tasks.loadFailed') }}
        </q-card-section>
        <q-card-section v-else class="text-muted">
          <q-spinner size="sm" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('tasks.close')" v-close-popup data-testid="task-dialog-close" />
        </q-card-actions>
      </template>
      <template v-else-if="data">
        <q-card-section class="scroll col" :style="$q.screen.lt.sm ? undefined : 'max-height: 80vh'">
          <div v-if="data.frozen" class="text-caption text-muted q-mb-sm" data-testid="task-frozen">{{ t('tasks.frozen') }}</div>
          <div v-if="data.fromRule" class="text-caption text-muted q-mb-xs">{{ t('tasks.fromRule') }}</div>
          <div v-if="data.ruleDeleted" class="text-caption text-warning q-mb-xs" data-testid="task-rule-deleted">{{ t('tasks.ruleDeleted') }}</div>
          <div class="q-gutter-y-sm">
            <div v-if="data.level === 'child' && data.parentTitle" class="text-caption text-muted" style="overflow-wrap: anywhere" data-testid="task-parent">
              <span>{{ t('tasks.subtask.parent') }}{{ t('tasks.colon') }}</span>
              <a v-if="data.parentVisible && data.parentTaskId" href="#" data-testid="task-parent-open" @click.prevent="emit('update:taskId', data.parentTaskId)">{{ data.parentTitle }}</a>
              <span v-else>{{ data.parentTitle }}</span>
            </div>
            <q-input v-model="draft.title" :label="t('tasks.fields.title')" maxlength="200" outlined dense :readonly="readonly" data-testid="task-title" />
            <div v-if="data.supervised" class="text-caption text-muted" data-testid="task-supervisor-hint">{{ t('tasks.supervisorHint') }}</div>
            <q-input v-model="draft.description" :label="t('tasks.fields.description')" type="textarea" autogrow maxlength="4000" outlined dense :readonly="readonly" data-testid="task-description" />
            <div class="row q-col-gutter-sm">
              <div class="col-12 col-sm-6">
                <GenericSelect v-model="draft.assignee" :label="t('tasks.fields.assignee')" :options="userOptions" :readonly="assigneeReadonly" data-testid="task-assignee" @update:model-value="(v) => (draft.assignee = v as string | null)" />
              </div>
              <div class="col-12 col-sm-6">
                <DateSelect v-model="draft.startDate" :label="t('tasks.fields.startDate')" :readonly="readonly" :error="startError" data-testid="task-start" />
              </div>
              <div class="col-12 col-sm-6">
                <DateSelect v-model="draft.dueDate" :label="t('tasks.fields.dueDate')" :readonly="readonly" data-testid="task-due" />
                <div v-if="startFollows" class="text-caption text-muted" data-testid="task-start-follows">{{ t('tasks.startFollows', { date: draft.dueDate }) }}</div>
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
          <template v-if="data.level !== 'child' && (data.level === 'parent' || data.canAddSubtask)">
            <q-separator class="q-my-md" />
            <SubtaskSection :task="data" @open="(id) => emit('update:taskId', id)" />
          </template>
          <q-separator class="q-my-md" />
          <div class="text-subtitle2">{{ t('tasks.comment') }}</div>
          <TaskTimeline :task-id="data.id" :comments="comments.data.value ?? []" :readonly="!data.canComment" />
          <MentionInput v-if="data.canComment" :task-id="data.id" :module-key="data.record?.moduleKey ?? null" />
        </q-card-section>
        <q-separator />
        <q-card-actions align="right">
          <q-btn v-if="data.canDelete" flat color="negative" no-caps :label="t('tasks.delete')" data-testid="task-delete" @click="confirming = true" />
          <q-space />
          <q-btn flat no-caps :label="t('tasks.close')" v-close-popup />
          <q-btn v-if="data.canEdit || data.canReassign" color="primary" no-caps :label="t('tasks.save')" :disable="!dirty || !!startError" :loading="update.isPending.value" data-testid="task-save" @click="save()" />
        </q-card-actions>
      </template>
      <q-dialog v-model="confirming">
        <q-card>
          <q-card-section style="overflow-wrap: anywhere" data-testid="task-delete-text">{{ data?.progress?.total ? t('tasks.subtask.confirmDelete', { n: data.progress.total }) : t('tasks.confirmDelete') }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('tasks.cancel')" v-close-popup />
            <q-btn color="negative" :label="t('tasks.delete')" :loading="del.isPending.value" data-testid="task-delete-confirm" @click="remove" />
          </q-card-actions>
        </q-card>
      </q-dialog>
      <SubtasksOpenDialog v-model="openChoice" :count="openCount" :busy="update.isPending.value" @choose="(c) => save(c)" />
    </q-card>
  </q-dialog>
</template>
