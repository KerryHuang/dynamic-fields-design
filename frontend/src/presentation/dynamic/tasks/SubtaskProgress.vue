<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { progressView, type TaskProgress } from 'src/domain/dynamic/task-types';

/**
 * 父任務的子任務進度：「完成數／（總數 − 取消數）」加進度條（ST-Q1）。有取消時滑過（觸控長按）提示「另有 N 個已取消」；
 * 全部取消或沒有子任務時整個不顯示（T20）。清單、看板卡片、任務詳細共用。
 */
const props = defineProps<{ progress: TaskProgress | null; bar?: boolean }>();
const { t } = useI18n();
const view = computed(() => progressView(props.progress));
const text = computed(() => (view.value ? `${view.value.done}／${view.value.denominator}` : ''));
const note = computed(() => (view.value && view.value.cancelled > 0 ? t('tasks.subtask.cancelledNote', { n: view.value.cancelled }) : ''));
const label = computed(() => `${t('tasks.subtask.progress')} ${text.value}${note.value ? `（${note.value}）` : ''}`);
</script>

<template>
  <div v-if="view" class="subtask-progress row items-center no-wrap" role="img" :aria-label="label" data-testid="subtask-progress">
    <q-icon name="account_tree" size="14px" class="q-mr-xs" />
    <span class="subtask-progress-text" data-testid="subtask-progress-text">{{ text }}</span>
    <q-linear-progress v-if="bar !== false" :value="view.ratio" rounded size="6px" class="subtask-progress-bar q-ml-sm" data-testid="subtask-progress-bar" />
    <q-tooltip v-if="note" anchor="top middle" self="bottom middle" data-testid="subtask-progress-note">{{ note }}</q-tooltip>
  </div>
</template>

<style scoped>
.subtask-progress { min-width: 0; max-width: 100%; cursor: default; }
.subtask-progress-text { white-space: nowrap; }
.subtask-progress-bar { flex: 1 1 40px; min-width: 40px; max-width: 120px; }
</style>
