<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useCreateTask } from 'src/application/dynamic/queries/tasks-queries';
import DateSelect from 'src/components/framework/DateSelect.vue';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { TaskDetail } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';

// Creates a task: on a record (recordId) or a personal one (no recordId).
const props = defineProps<{ modelValue: boolean; recordId?: number; defaultAssignee?: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; created: [task: TaskDetail] }>();
const { t } = useI18n();
const users = useUsers();
const create = useCreateTask();

const title = ref('');
const description = ref('');
const assignee = ref<string | null>(null);
const dueDate = ref<string | null>(null);
const error = ref('');
const titleError = ref('');

// Start blank every time it opens, so a cancelled draft does not leak into the next task.
watch(() => props.modelValue, (open) => {
  if (!open) return;
  title.value = '';
  description.value = '';
  assignee.value = props.defaultAssignee ?? null;
  dueDate.value = null;
  error.value = '';
  titleError.value = '';
});

const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName })));

async function submit() {
  const text = title.value.trim();
  titleError.value = text ? '' : t('tasks.titleRequired');
  if (!text) return;
  error.value = '';
  try {
    const task = await create.mutateAsync({
      recordId: props.recordId,
      body: { title: text, description: description.value.trim() || null, assigneeUserId: assignee.value, dueDate: dueDate.value },
    });
    emit('created', task);
    emit('update:modelValue', false);
  } catch (e) {
    const err = e as ApiError;
    error.value = (err.errors ? Object.values(err.errors).flat().join('、') : err.message) || t('tasks.saveFailed');
  }
}
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="width: 480px; max-width: 95vw" data-testid="task-form">
      <q-card-section class="text-h6">{{ t('tasks.createTitle') }}</q-card-section>
      <q-card-section class="q-gutter-md">
        <q-input
          v-model="title" :label="`${t('tasks.fields.title')} *`" maxlength="200" outlined dense autofocus
          :error="!!titleError" :error-message="titleError" data-testid="task-form-title" @keyup.enter="submit"
        />
        <q-input v-model="description" :label="t('tasks.fields.description')" type="textarea" maxlength="4000" autogrow outlined dense data-testid="task-form-description" />
        <GenericSelect v-model="assignee" :label="t('tasks.fields.assignee')" :options="userOptions" data-testid="task-form-assignee" @update:model-value="(v) => (assignee = v as string | null)" />
        <DateSelect v-model="dueDate" :label="t('tasks.fields.dueDate')" />
        <div v-if="error" class="text-negative text-caption" data-testid="task-form-error">{{ error }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('tasks.cancel')" v-close-popup />
        <q-btn color="primary" :label="t('tasks.create')" :loading="create.isPending.value" data-testid="task-form-submit" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
