<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { isLaterDateAllowed } from 'src/domain/dynamic/task-board-types';

/** 拖到「之後」欄時選日期：預設是伺服器給的 laterDefault，只能選本週最後一天（weekEnd）之後；取消就放回、不送請求。 */
const props = defineProps<{ modelValue: boolean; weekEnd: string; defaultDate: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; confirm: [date: string] }>();
const { t } = useI18n();

const date = ref(props.defaultDate);
watch(() => props.modelValue, (open) => { if (open) date.value = props.defaultDate; });
const valid = computed(() => isLaterDateAllowed(date.value, props.weekEnd));
// q-date 的 options 函式拿到的永遠是 YYYY/MM/DD
const allowed = (d: string) => isLaterDateAllowed(d.replace(/\//g, '-'), props.weekEnd);
function submit() {
  if (!valid.value) return;
  emit('confirm', date.value);
  emit('update:modelValue', false);
}
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 340px; max-width: 95vw'" data-testid="due-pick">
      <q-card-section class="text-h6">{{ t('taskBoard.duePick.title') }}</q-card-section>
      <q-card-section class="q-pt-none">
        <div class="text-caption text-muted q-mb-sm">{{ t('taskBoard.duePick.hint', { date: weekEnd }) }}</div>
        <q-date v-model="date" mask="YYYY-MM-DD" :options="allowed" minimal flat data-testid="due-pick-date" />
        <div v-if="!valid" class="text-negative text-caption q-mt-xs" role="alert" data-testid="due-pick-invalid">{{ t('taskBoard.duePick.invalid', { date: weekEnd }) }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps :label="t('tasks.cancel')" data-testid="due-pick-cancel" />
        <q-btn color="primary" no-caps :label="t('taskBoard.duePick.ok')" :disable="!valid" data-testid="due-pick-ok" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
