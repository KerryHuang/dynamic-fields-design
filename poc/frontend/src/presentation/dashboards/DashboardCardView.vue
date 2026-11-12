<script setup lang="ts">
import { computed, ref, toRef } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRunCard } from 'src/application/dynamic/queries/dashboards-queries';
import type { DashboardCard } from 'src/domain/dynamic/dashboard-types';
import type { ReportColumn, ReportDateRange } from 'src/domain/dynamic/report-types';
import ReportResultView from 'src/presentation/reports/ReportResultView.vue';
import { formatValue } from 'src/presentation/reports/report-format';
import CardDrillDialog from './CardDrillDialog.vue';

/** 一張卡片：各自查詢、各自顯示 spec §9 的狀態；一張壞掉不影響其他張。 */
const props = defineProps<{
  dashboardId: number; card: DashboardCard; reportName?: string; filterRange: ReportDateRange | null; editing: boolean;
  /** 連結的目標儀表板觀看者看得到才顯示連結。 */
  linkVisible: boolean;
}>();
const emit = defineEmits<{ settings: [cardId: string, columns: ReportColumn[]]; remove: [cardId: string] }>();
const { t, locale } = useI18n();

// 卡片設定（含呈現方式）變了就換 key 重查；暫時的日期篩選本來就在 key 裡
const signature = computed(() => JSON.stringify([props.card.viz, props.card.options]));
const run = useRunCard(toRef(props, 'dashboardId'), computed(() => props.card.id), toRef(props, 'filterRange'), locale, signature);
const result = computed(() => run.data.value);
const drillKeys = ref<string[] | null>(null);

// 圖表高度跟著卡片的列數走（一列 70px 加間距），扣掉標題與說明文字
const chartHeight = computed(() => `${Math.max(140, props.card.h * 82 - 130)}px`);
const title = computed(() => props.card.title || props.reportName || t('dashboards.card.untitled'));
const firstMeasure = computed(() => result.value?.columns.find((c) => c.role === 'measure'));
const compareText = computed(() => {
  const c = result.value?.compare;
  const m = firstMeasure.value;
  if (!c || !m || !props.card.options.comparePeriods) return '';
  if (c.deltaPercent == null) return t('dashboards.card.compareNoDelta', { n: props.card.options.comparePeriods, avg: formatValue(c.previousAverage ?? null, m.valueType, locale.value) });
  const sign = c.deltaPercent > 0 ? '+' : '';
  return t('dashboards.card.compare', {
    n: props.card.options.comparePeriods, avg: formatValue(c.previousAverage ?? null, m.valueType, locale.value),
    delta: `${sign}${c.deltaPercent.toLocaleString(locale.value, { maximumFractionDigits: 1 })}%`,
  });
});
const deltaClass = computed(() => ((result.value?.compare?.deltaPercent ?? 0) >= 0 ? 'text-positive' : 'text-negative'));
</script>

<template>
  <q-card flat bordered class="dashboard-card column no-wrap" :data-testid="`card-${card.id}`">
    <div class="row items-center no-wrap q-px-sm q-pt-xs" :class="{ 'card-drag-handle cursor-move': editing }">
      <div class="col text-subtitle2 ellipsis" :title="title" data-testid="card-title">{{ title }}</div>
      <q-badge v-if="card.options.topN" outline class="q-mr-xs" data-testid="card-topn">{{ t('dashboards.card.topN', { n: card.options.topN }) }}</q-badge>
      <q-btn
        v-if="linkVisible && card.options.linkDashboardId" flat dense round size="sm" icon="open_in_new"
        :to="`/dashboards/${card.options.linkDashboardId}`" :aria-label="t('dashboards.card.link')" data-testid="card-link"
      />
      <template v-if="editing">
        <q-btn flat dense round size="sm" icon="tune" :aria-label="t('dashboards.card.settings')" :data-testid="`card-settings-${card.id}`" @click="emit('settings', card.id, result?.columns ?? [])" />
        <q-btn flat dense round size="sm" icon="close" :aria-label="t('dashboards.card.remove')" :data-testid="`card-remove-${card.id}`" @click="emit('remove', card.id)" />
      </template>
    </div>
    <div class="col q-pa-sm card-body">
      <q-inner-loading :showing="run.isFetching.value && !result" />
      <div v-if="run.isError.value" class="column items-start" data-testid="card-error">
        <span class="text-negative">{{ t('dashboards.card.loadFailed') }}</span>
        <q-btn flat dense no-caps color="primary" :label="t('dashboards.card.retry')" data-testid="card-retry" @click="run.refetch()" />
      </div>
      <template v-else-if="result">
        <ReportResultView :result="result" drillable :viz="card.viz" :chart-height="chartHeight" @drill="(keys: string[]) => (drillKeys = keys)" />
        <div v-if="compareText" class="text-caption q-mt-xs" :class="deltaClass" data-testid="card-compare">{{ compareText }}</div>
        <div v-if="result.ignoresDateFilter" class="text-caption text-muted q-mt-xs" data-testid="card-ignores-filter">{{ t('dashboards.card.ignoresFilter') }}</div>
      </template>
    </div>
    <CardDrillDialog
      :dashboard-id="dashboardId" :card-id="card.id" :group-keys="drillKeys" :filter-range="filterRange" @close="drillKeys = null"
    />
  </q-card>
</template>

<style scoped>
.dashboard-card {
  height: 100%;
}
.card-body {
  position: relative;
  min-height: 0;
  overflow: auto;
}
</style>
