<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { usePreviewCard } from 'src/application/dynamic/queries/dashboards-queries';
import type { DashboardCard } from 'src/domain/dynamic/dashboard-types';
import ReportResultView from 'src/presentation/reports/ReportResultView.vue';

/** 指派版編輯時卡片內的預覽：後端以管理員自己的權限算；沒有權限、看不到報表或查詢失敗一律顯示「無法預覽」，不顯示數字。 */
const props = defineProps<{ card: DashboardCard }>();
const { t, locale } = useI18n();
const run = usePreviewCard(computed(() => props.card), locale);
const result = computed(() => run.data.value);
const usable = computed(() => result.value?.state === 'ok' || result.value?.state === 'empty');
const chartHeight = computed(() => `${Math.max(120, props.card.h * 82 - 150)}px`);
</script>

<template>
  <div class="admin-card-preview" :data-testid="`admin-preview-${card.id}`">
    <q-inner-loading :showing="run.isFetching.value && !result" />
    <div v-if="run.isError.value || (result && !usable)" class="text-muted" :data-testid="`admin-preview-unavailable-${card.id}`">
      {{ t('dashboards.admin.previewUnavailable') }}
    </div>
    <ReportResultView v-else-if="result" :result="result" :viz="card.viz" :chart-height="chartHeight" />
  </div>
</template>

<style scoped>
.admin-card-preview {
  position: relative;
  min-height: 0;
  overflow: auto;
  padding: 0 var(--t-space-sm);
}
</style>
