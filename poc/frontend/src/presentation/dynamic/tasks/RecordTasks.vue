<script setup lang="ts">
import { computed, ref } from 'vue';
import { useQuasar } from 'quasar';
import { useI18n } from 'vue-i18n';
import { useRecordTasks, useUpdateTask } from 'src/application/dynamic/queries/tasks-queries';
import { taskStatusColor, type TaskSummary } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import TaskDialog from './TaskDialog.vue';
import TaskFormDialog from './TaskFormDialog.vue';

const props = defineProps<{ recordId: number; closed: boolean }>();
const { t } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();
const query = useRecordTasks(() => props.recordId);
const update = useUpdateTask();
const items = computed(() => query.data.value?.items ?? []);
const canAdd = computed(() => !!query.data.value?.canAdd && !props.closed);

const openId = ref<number | null>(null);
const adding = ref(false);

const locked = (task: TaskSummary) => props.closed || !task.canEdit;
// Unticking a finished task reopens it as "to do"; ticking finishes it.
const toggle = (task: TaskSummary, done: boolean) =>
  update.mutate(
    { id: task.id, patch: { status: done ? 'done' : 'todo' } },
    { onError: (e) => $q.notify({ type: 'negative', message: (e as unknown as ApiError).message || t('tasks.saveFailed') }) },
  );
</script>

<template>
  <q-card v-if="query.data.value" flat bordered class="q-mt-md" data-testid="record-tasks">
    <q-card-section class="row items-center no-wrap q-pb-none">
      <div class="text-subtitle1">{{ t('tasks.title') }}</div>
      <q-space />
      <q-btn v-if="canAdd" flat dense color="primary" icon="add" :label="t('tasks.add')" data-testid="task-add" @click="adding = true" />
    </q-card-section>
    <div v-if="!items.length" class="q-pa-md text-muted" data-testid="tasks-empty">{{ t('tasks.empty') }}</div>
    <q-list v-else separator>
      <q-item v-for="task in items" :key="task.id" :data-testid="`task-row-${task.id}`">
        <q-item-section side top>
          <q-checkbox
            :model-value="task.status === 'done'" :disable="locked(task)" :data-testid="`task-check-${task.id}`"
            :aria-label="t('tasks.status.done')" @update:model-value="(v: boolean) => toggle(task, v)"
          />
        </q-item-section>
        <q-item-section class="cursor-pointer" @click="openId = task.id">
          <q-item-label :class="{ 'text-strike text-muted': task.status === 'done' || task.status === 'cancelled' }" style="overflow-wrap: anywhere">{{ task.title }}</q-item-label>
          <q-item-label caption class="row items-center q-gutter-x-sm">
            <span :data-testid="`task-assignee-${task.id}`">{{ task.assigneeName ?? (task.assigneeUserId || t('tasks.unassigned')) }}</span>
            <span v-if="task.dueDate" :class="{ 'text-negative': task.overdue }" :data-testid="`task-due-${task.id}`">{{ task.dueDate }}</span>
            <q-badge outline :color="taskStatusColor(task.status)" :label="t(`tasks.status.${task.status}`)" />
          </q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
    <TaskFormDialog v-model="adding" :record-id="recordId" :default-assignee="identity.userId" />
    <TaskDialog :task-id="openId" @update:task-id="openId = $event" />
  </q-card>
</template>
