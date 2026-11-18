<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import type { TaskCascade } from 'src/domain/dynamic/task-types';

/**
 * 父任務改成完成、還有未完成子任務時的三選一確認（後端 409 subtasksOpen）：一併完成、一併取消、取消操作。
 * 「取消操作」就是關掉框、不重送。手機上按鈕直排、框滿版，不橫捲。
 */
defineProps<{ modelValue: boolean; count: number; busy?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; choose: [cascade: TaskCascade] }>();
const { t } = useI18n();
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 420px; max-width: 95vw'" data-testid="subtasks-open">
      <q-card-section class="text-h6">{{ t('tasks.subtask.openTitle') }}</q-card-section>
      <q-card-section class="q-pt-none" style="overflow-wrap: anywhere" data-testid="subtasks-open-body">{{ t('tasks.subtask.openBody', { n: count }) }}</q-card-section>
      <q-card-actions :vertical="$q.screen.lt.sm" :align="$q.screen.lt.sm ? undefined : 'right'" :class="{ 'q-gutter-y-xs': $q.screen.lt.sm }">
        <q-btn v-close-popup flat no-caps :label="t('tasks.subtask.openAbort')" data-testid="subtasks-open-abort" />
        <q-btn outline color="negative" no-caps :label="t('tasks.subtask.openCancelAll')" :disable="busy" data-testid="subtasks-open-cancel" @click="emit('choose', 'cancel')" />
        <q-btn color="primary" no-caps :label="t('tasks.subtask.openCompleteAll')" :disable="busy" data-testid="subtasks-open-complete" @click="emit('choose', 'complete')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
