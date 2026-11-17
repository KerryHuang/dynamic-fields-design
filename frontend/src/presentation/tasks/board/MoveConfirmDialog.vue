<script setup lang="ts">
import { useI18n } from 'vue-i18n';

/** 拖到「取消」或清空規則任務到期日前的確認框（U5）。 */
defineProps<{ modelValue: boolean; title: string; body: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; confirm: [] }>();
const { t } = useI18n();
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 360px; max-width: 95vw'" data-testid="move-confirm">
      <q-card-section class="text-h6">{{ title }}</q-card-section>
      <q-card-section class="q-pt-none" data-testid="move-confirm-body">{{ body }}</q-card-section>
      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps :label="t('tasks.cancel')" data-testid="move-confirm-cancel" />
        <q-btn v-close-popup color="primary" no-caps :label="t('taskBoard.confirm.ok')" data-testid="move-confirm-ok" @click="emit('confirm')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
