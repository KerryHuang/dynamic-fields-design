<script setup lang="ts">
import { Notify } from 'quasar';
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import { notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import { dynamicApi, type ExportQuery } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';

const props = defineProps<{ schema: ModuleSchema; query: ExportQuery }>();
const { t, locale } = useI18n();
const busy = ref(false);

async function download(format: 'csv' | 'xlsx') {
  busy.value = true;
  try {
    const { blob, fileName } = await dynamicApi.exportRecords(props.schema.key, format, props.query, locale.value);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    // 立刻 revoke 可能讓瀏覽器還沒開始下載就失效，延後釋放
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (e) {
    const err = e as ApiError;
    if (isForbidden(err)) Notify.create({ type: 'negative', message: t('dynamic.forbidden') });
    else notifyApiError(Notify.create, err, [], t('dynamic.loadFailed'));
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <q-btn-dropdown
    v-if="schema.permissions.printExport" outline dense icon="download" data-testid="export-btn"
    :label="t('dynamic.export.title')" :loading="busy"
  >
    <q-list>
      <q-item v-close-popup clickable data-testid="export-csv" @click="download('csv')">
        <q-item-section>{{ t('dynamic.export.csv') }}</q-item-section>
      </q-item>
      <q-item v-close-popup clickable data-testid="export-xlsx" @click="download('xlsx')">
        <q-item-section>{{ t('dynamic.export.excel') }}</q-item-section>
      </q-item>
    </q-list>
  </q-btn-dropdown>
</template>
