<script setup lang="ts">
import { useI18n } from 'vue-i18n';

/** 撤銷這類不可復原動作的二次確認（個人頁與管理員頁共用）；`testid` 是前綴：`<testid>` 對話框、`<testid>-yes` 確認鈕。 */
defineProps<{ modelValue: boolean; title: string; body: string; confirmLabel: string; testid: string; loading?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; confirm: [] }>();
const { t } = useI18n();
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="width: 400px; max-width: 95vw" :data-testid="testid">
      <q-card-section class="text-h6" style="overflow-wrap: anywhere">{{ title }}</q-card-section>
      <q-card-section class="q-pt-none" style="overflow-wrap: anywhere">{{ body }}</q-card-section>
      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps :label="t('dynamic.cancel')" :data-testid="`${testid}-no`" />
        <q-btn color="negative" no-caps :label="confirmLabel" :loading="loading" :data-testid="`${testid}-yes`" @click="emit('confirm')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
