<script setup lang="ts">
import { computed, ref } from 'vue';
import { useQuasar } from 'quasar';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { useApplyTaskRule, useRecordTasks, useUpdateTask } from 'src/application/dynamic/queries/tasks-queries';
import { taskStatusColor, type ManualRule, type TaskSummary } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import TaskDialog from './TaskDialog.vue';
import TaskFormDialog from './TaskFormDialog.vue';

const props = defineProps<{ recordId: number; closed: boolean }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();
const query = useRecordTasks(() => props.recordId);
const update = useUpdateTask();
const apply = useApplyTaskRule();
const items = computed(() => query.data.value?.items ?? []);
const canAdd = computed(() => !!query.data.value?.canAdd && !props.closed);

const manualRules = computed(() => query.data.value?.manualRules ?? []);

const openId = ref<number | null>(null);
const adding = ref(false);

const locked = (task: TaskSummary) => props.closed || !task.canEdit;
// Unticking a finished task reopens it as "to do"; ticking finishes it.
const toggle = (task: TaskSummary, done: boolean) =>
  update.mutate(
    { id: task.id, patch: { status: done ? 'done' : 'todo' } },
    { onError: (e) => $q.notify({ type: 'negative', message: (e as unknown as ApiError).message || t('tasks.saveFailed') }) },
  );

// once: "applied" is final. everyTime never counts as applied; it waits until the previous round is done.
const applyState = (r: ManualRule) => (r.repeat === 'once' ? (r.applied ? t('tasks.applied') : '') : (r.previousOpen ? t('tasks.previousRoundOpen') : ''));
const applyDisabled = (r: ManualRule) => !r.applicable || !!applyState(r) || apply.isPending.value;
const applyRule = (r: ManualRule) =>
  apply.mutate(
    { recordId: props.recordId, ruleKey: r.key },
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
    <div v-if="canAdd && manualRules.length" class="column q-px-md q-pt-sm q-gutter-y-xs" data-testid="task-apply-list">
      <div v-for="r in manualRules" :key="r.key" class="row items-center q-gutter-x-sm">
        <q-btn
          outline dense no-caps class="task-apply-btn" color="primary" icon="playlist_add" :label="t('tasks.applyTemplate', { name: resolveI18n(r.name, locale) })"
          :disable="applyDisabled(r)" :data-testid="`task-apply-${r.key}`" style="max-width: 100%" @click="applyRule(r)"
        />
        <span v-if="applyState(r)" class="text-caption text-muted" :data-testid="`task-apply-state-${r.key}`">{{ applyState(r) }}</span>
      </div>
    </div>
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

<style scoped>
/* A long rule name wraps inside the button instead of pushing the page wider than a phone. */
.task-apply-btn :deep(.q-btn__content) { white-space: normal; text-align: left; overflow-wrap: anywhere; }
</style>
