<script setup lang="ts">
import { computed, toRef } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDrillReport } from 'src/application/dynamic/queries/reports-queries';
import { NO_RUN_FILTER, type ReportRunFilter } from 'src/domain/dynamic/report-types';
import ReportResultView from './ReportResultView.vue';

/** filter 是報表頁目前的暫時篩選，下鑽的明細與畫面同一範圍；openableTasks 時點一列由父層開任務詳細。 */
const props = withDefaults(defineProps<{ reportId: number; groupKeys: string[] | null; filter?: ReportRunFilter; openableTasks?: boolean }>(), { filter: () => NO_RUN_FILTER, openableTasks: false });
const emit = defineEmits<{ close: []; openTask: [taskId: number] }>();
const { t, locale } = useI18n();
const drill = useDrillReport(toRef(props, 'reportId'), toRef(props, 'groupKeys'), locale, toRef(props, 'filter'));
const result = computed(() => drill.data.value);
</script>

<template>
  <q-dialog :model-value="groupKeys !== null" @update:model-value="emit('close')">
    <q-card style="width: 960px; max-width: 95vw">
      <q-card-section class="text-h6">{{ t('reports.drillTitle') }}</q-card-section>
      <q-card-section style="max-height: 70vh; overflow: auto">
        <q-inner-loading :showing="drill.isFetching.value" />
        <div v-if="drill.isError.value" class="text-negative" data-testid="drill-error">{{ t('reports.loadFailed') }}</div>
        <template v-else-if="result">
          <div v-if="result.truncated" class="text-warning q-mb-sm" data-testid="drill-truncated">{{ t('reports.drillTruncated', { n: result.detailRows?.length ?? 0 }) }}</div>
          <ReportResultView :result="result" viz="table" :openable-tasks="openableTasks" @open-task="emit('openTask', $event)" />
        </template>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('reports.close')" data-testid="drill-close" @click="emit('close')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
