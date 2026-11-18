<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useCreateSubtask } from 'src/application/dynamic/queries/tasks-queries';
import DateSelect from 'src/components/framework/DateSelect.vue';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import { taskStatusColor, type TaskDetail } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import SubtaskProgress from './SubtaskProgress.vue';

/**
 * 任務詳細的「子任務」區塊（§10.2）：進度、子任務清單、新增（只填標題、負責人、到期日）。
 * 負責人預填父任務的負責人，父任務負責人已停用時預填空白；到期日預設空（ST-T2，後端不補預設）。
 * 子任務本身不顯示這個區塊（只有一層）。點一列開那個子任務的詳細。
 */
const props = defineProps<{ task: TaskDetail }>();
const emit = defineEmits<{ open: [id: number] }>();
const { t } = useI18n();
const users = useUsers();
const create = useCreateSubtask();

const adding = ref(false);
const title = ref('');
const assignee = ref<string | null>(null);
const dueDate = ref<string | null>(null);
const titleError = ref('');
const error = ref('');

const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName })));
const defaultAssignee = () => (props.task.assigneeUserId && props.task.assigneeActive ? props.task.assigneeUserId : null);

function startAdding() {
  title.value = '';
  assignee.value = defaultAssignee();
  dueDate.value = null;
  titleError.value = '';
  error.value = '';
  adding.value = true;
}

let submitting = false;
async function submit() {
  // 連按 Enter 或連點按鈕：上一次還沒回來就不再送（isPending 要等下一個 tick 才反映）
  if (submitting) return;
  const text = title.value.trim();
  titleError.value = text ? '' : t('tasks.titleRequired');
  if (!text) return;
  error.value = '';
  submitting = true;
  try {
    await create.mutateAsync({ parentId: props.task.id, body: { title: text, assigneeUserId: assignee.value, dueDate: dueDate.value } });
    // 連續新增：留著表單與負責人，只清標題與到期日
    title.value = '';
    dueDate.value = null;
  } catch (e) {
    const err = e as ApiError;
    error.value = (err.errors ? Object.values(err.errors).flat().join(t('common.listSeparator')) : err.message) || t('tasks.saveFailed');
  } finally {
    submitting = false;
  }
}

const assigneeLabel = (s: TaskDetail['subtasks'][number]) =>
  s.assigneeName ?? (s.assigneeUserId ? s.assigneeUserId : t('tasks.unassigned'));
const closed = (s: TaskDetail['subtasks'][number]) => s.status === 'done' || s.status === 'cancelled';
</script>

<template>
  <section class="subtask-section" data-testid="subtask-section">
    <div class="row items-center no-wrap q-mb-xs" style="gap: 8px">
      <div class="text-subtitle2">{{ t('tasks.subtask.title') }}</div>
      <SubtaskProgress :progress="task.progress" class="text-caption text-muted col" />
      <q-space v-if="!task.progress" />
      <q-btn
        v-if="task.canAddSubtask && !adding" flat dense no-caps color="primary" icon="add" :label="t('tasks.subtask.add')"
        data-testid="subtask-add" @click="startAdding"
      />
    </div>
    <div v-if="!task.subtasks.length && !adding" class="text-caption text-muted" data-testid="subtask-empty">{{ t('tasks.subtask.empty') }}</div>
    <q-list v-if="task.subtasks.length" separator bordered dense class="rounded-borders" data-testid="subtask-list">
      <q-item v-for="s in task.subtasks" :key="s.id" clickable :data-testid="`subtask-row-${s.id}`" @click="emit('open', s.id)">
        <q-item-section>
          <q-item-label :class="{ 'text-strike text-muted': closed(s) }" style="overflow-wrap: anywhere" :data-testid="`subtask-title-${s.id}`">{{ s.title }}</q-item-label>
          <q-item-label caption class="row items-center q-gutter-x-sm">
            <span :data-testid="`subtask-assignee-${s.id}`" style="overflow-wrap: anywhere">{{ assigneeLabel(s) }}</span>
            <span v-if="s.dueDate" :class="{ 'text-negative': s.overdue }">{{ s.dueDate }}</span>
            <q-badge outline :color="taskStatusColor(s.status)" :label="t(`tasks.status.${s.status}`)" />
          </q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
    <div v-if="adding" class="q-mt-sm q-gutter-y-sm" data-testid="subtask-form">
      <q-input
        v-model="title" :label="`${t('tasks.subtask.titleLabel')} *`" maxlength="200" outlined dense autofocus
        :error="!!titleError" :error-message="titleError" data-testid="subtask-form-title" @keyup.enter="submit"
      />
      <div class="row q-col-gutter-sm">
        <div class="col-12 col-sm-6">
          <GenericSelect v-model="assignee" :label="t('tasks.fields.assignee')" :options="userOptions" data-testid="subtask-form-assignee" @update:model-value="(v) => (assignee = v as string | null)" />
        </div>
        <div class="col-12 col-sm-6">
          <DateSelect v-model="dueDate" :label="t('tasks.fields.dueDate')" />
        </div>
      </div>
      <div v-if="error" class="text-negative text-caption" role="alert" data-testid="subtask-form-error">{{ error }}</div>
      <div class="row justify-end q-gutter-x-sm">
        <q-btn flat no-caps :label="t('tasks.subtask.formDone')" data-testid="subtask-form-close" @click="adding = false" />
        <q-btn color="primary" no-caps :label="t('tasks.subtask.formSubmit')" :loading="create.isPending.value" data-testid="subtask-form-submit" @click="submit" />
      </div>
    </div>
  </section>
</template>
