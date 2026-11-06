<script setup lang="ts">
import { computed, toRef } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDrillReport } from 'src/application/dynamic/queries/reports-queries';
import ReportResultView from './ReportResultView.vue';

const props = defineProps<{ reportId: number; groupKeys: string[] | null }>();
const emit = defineEmits<{ close: [] }>();
const { t, locale } = useI18n();
const drill = useDrillReport(toRef(props, 'reportId'), toRef(props, 'groupKeys'), locale);
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
          <div v-if="result.truncated" class="text-warning q-mb-sm" data-testid="drill-truncated">{{ t('reports.drillTruncated') }}</div>
          <ReportResultView :result="result" viz="table" />
        </template>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('reports.close')" data-testid="drill-close" @click="emit('close')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
