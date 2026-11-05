<script setup lang="ts">
import { date } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRecordHistory } from 'src/application/dynamic/queries/records-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { RecordHistoryEntry } from 'src/domain/dynamic/types';

const props = defineProps<{ recordId: number }>();
const { t, locale } = useI18n();
const SIZE = 20;

const recordId = computed(() => props.recordId);
const page = ref(1);
const size = ref(SIZE);
// 換記錄時回第 1 頁；sync 讓查詢不會先用新記錄配舊頁碼打一次
watch(recordId, () => { page.value = 1; }, { flush: 'sync' });
const query = useRecordHistory(recordId, page, size, locale);
const rows = computed(() => query.data.value?.items ?? []);
const pageCount = computed(() => Math.max(1, Math.ceil((query.data.value?.totalCount ?? 0) / SIZE)));

const columns = computed(() => ['time', 'user', 'field', 'oldValue', 'newValue'].map((name) => ({
  name, label: t(`dynamic.history.${name}`), field: name, align: 'left' as const,
})));

const timeText = (x: RecordHistoryEntry) => date.formatDate(new Date(x.changedTime), 'YYYY-MM-DD HH:mm');

function fieldText(x: RecordHistoryEntry) {
  const label = x.operation === 'create' ? t('dynamic.history.created')
    : x.operation === 'delete' ? t('dynamic.history.deleted')
      : resolveI18n(x.fieldLabel, locale.value);
  if (!x.detail) return label;
  const name = resolveI18n(x.detail.moduleName, locale.value);
  return `${name}${x.detail.title ? ` › ${x.detail.title}` : ''}：${label}`;
}
</script>

<template>
  <q-card flat bordered class="q-mt-md" data-testid="record-history">
    <q-card-section class="text-subtitle1">{{ t('dynamic.history.title') }}</q-card-section>
    <q-table flat dense hide-pagination :rows="rows" :columns="columns" row-key="id" :rows-per-page-options="[0]"
      :no-data-label="t('dynamic.history.empty')">
      <template #body="{ row }">
        <q-tr :props="{ row }" data-testid="history-row">
          <q-td>{{ timeText(row) }}</q-td>
          <q-td>{{ row.changedUserName }}</q-td>
          <q-td v-if="row.operation === 'update'">{{ fieldText(row) }}</q-td>
          <q-td v-else colspan="3">{{ fieldText(row) }}</q-td>
          <template v-if="row.operation === 'update'">
            <q-td>{{ row.oldText }}</q-td>
            <q-td>{{ row.newText }}</q-td>
          </template>
        </q-tr>
      </template>
    </q-table>
    <div v-if="pageCount > 1" class="flex flex-center q-pa-sm">
      <q-pagination v-model="page" :max="pageCount" direction-links data-testid="history-pagination" />
    </div>
  </q-card>
</template>
