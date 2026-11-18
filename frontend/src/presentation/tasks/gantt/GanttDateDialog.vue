<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import DateSelect from 'src/components/framework/DateSelect.vue';
import type { BoardCard } from 'src/domain/dynamic/task-board-types';
import { startAfterDue } from 'src/domain/dynamic/task-types';
import type { ScheduleFieldErrors, ScheduleOutcome } from './use-gantt-schedule';

/**
 * 甘特圖的「日期」對話框（看板設計 §11.5.4.9）：平板點長條、任何寬度的「編輯日期」與未排程的「排程」都開它。
 * 開始日、到期日都可清空（兩個都清空＝移到未排程）；開始日晚於到期日即時提示、不能儲存；伺服器 400 掛在欄位下、對話框不關，其他失敗由 save 跳通知後關閉。
 */
const props = defineProps<{
  modelValue: boolean;
  card: BoardCard | null;
  save: (card: BoardCard, startDate: string | null, dueDate: string | null) => Promise<ScheduleOutcome | null>;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
const { t } = useI18n();

const start = ref<string | null>(null);
const due = ref<string | null>(null);
const server = ref<ScheduleFieldErrors>({});
const saving = ref(false);
watch(() => props.modelValue, (open) => {
  if (!open || !props.card) return;
  start.value = props.card.startDate;
  due.value = props.card.dueDate;
  server.value = {};
}, { immediate: true });
watch([start, due], () => { server.value = {}; });

const ISO = /^\d{4}-\d{2}-\d{2}$/;
const badFormat = (v: string | null) => v !== null && !ISO.test(v);
const startError = computed(() => {
  if (badFormat(start.value)) return t('dynamic.validation.date');
  return startAfterDue(start.value, due.value) ? t('tasks.startAfterDue') : (server.value.startDate ?? '');
});
const dueError = computed(() => (badFormat(due.value) ? t('dynamic.validation.date') : (server.value.dueDate ?? '')));
const changed = computed(() => !!props.card && (start.value !== props.card.startDate || due.value !== props.card.dueDate));
const canSave = computed(() => changed.value && !startError.value && !dueError.value && !saving.value);

async function submit() {
  const card = props.card;
  if (!card || !canSave.value) return;
  saving.value = true;
  try {
    const outcome = await props.save(card, start.value, due.value);
    if (outcome && !outcome.ok && outcome.fields) {
      server.value = outcome.fields;
      return;
    }
    emit('update:modelValue', false);
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="width: 360px; max-width: 95vw" data-testid="gantt-dates">
      <q-form @submit="submit">
        <q-card-section class="q-pb-sm">
          <div class="text-h6">{{ t('taskGantt.schedule.title') }}</div>
          <div class="text-body2 text-muted gantt-dates-task" data-testid="gantt-dates-task">{{ card?.title }}</div>
        </q-card-section>
        <q-card-section class="q-pt-none column q-gutter-y-sm">
          <DateSelect v-model="start" :label="t('tasks.fields.startDate')" :error="startError" clearable data-testid="gantt-dates-start" />
          <DateSelect v-model="due" :label="t('tasks.fields.dueDate')" :error="dueError" clearable data-testid="gantt-dates-due" />
          <div class="text-caption text-muted">{{ t('taskGantt.schedule.clearHint') }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps :label="t('tasks.cancel')" data-testid="gantt-dates-cancel" />
          <q-btn type="submit" color="primary" no-caps :label="t('tasks.save')" :disable="!canSave" :loading="saving" data-testid="gantt-dates-save" />
        </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.gantt-dates-task { overflow-wrap: anywhere; }
</style>
