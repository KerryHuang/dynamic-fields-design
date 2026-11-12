<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { CardResult } from 'src/domain/dynamic/dashboard-types';
import type { ReportColumn, ReportResult } from 'src/domain/dynamic/report-types';
import ReportChart from './ReportChart.vue';
import { formatValue } from './report-format';

const props = defineProps<{ result: ReportResult; viz: string; chartHeight?: string; drillable?: boolean }>();
const emit = defineEmits<{ drill: [groupKeys: string[]] }>();
const { t, locale } = useI18n();

const STATE_KEYS = { empty: 'reports.state.empty', restricted: 'reports.state.restricted', fieldMissing: 'reports.state.fieldMissing',
  moduleDisabled: 'reports.state.moduleDisabled', tooMany: 'reports.state.tooMany', unshared: 'reports.state.unshared',
  reportGone: 'reports.state.reportGone' } as const;
// 這幾種狀態的後端訊息帶了具體原因（欄位名稱、哪一個上限），優先顯示
const stateText = computed(() => {
  const s = props.result.state;
  if (s === 'ok') return '';
  const key = STATE_KEYS[s];
  return (s === 'restricted' || s === 'fieldMissing' || s === 'tooMany') && props.result.message ? props.result.message : t(key);
});

const groupCols = computed(() => props.result.columns.filter((c) => c.role === 'group'));
const measureCols = computed(() => props.result.columns.filter((c) => c.role === 'measure'));
const fieldCols = computed(() => props.result.columns.filter((c) => c.role === 'field'));
const groups = computed(() => props.result.groups ?? []);
const hasGroups = computed(() => groups.value.length > 0);
const hasDetail = computed(() => !!props.result.detailRows?.length);
const chartViz = computed(() => (['line', 'bar', 'donut'] as const).find((v) => v === props.viz));
const showChart = computed(() => !!chartViz.value && hasGroups.value && measureCols.value.length > 0);
// 指標卡，或沒有分組也沒有明細時只剩總計可看
const showIndicator = computed(() => measureCols.value.length > 0 && (props.viz === 'indicator' || (!hasGroups.value && !hasDetail.value)));
const showGroupTable = computed(() => hasGroups.value && !showChart.value && !showIndicator.value);
const fmt = (v: string | number | null | undefined, c: ReportColumn) => formatValue(v, c.valueType, locale.value);
const range = computed(() => props.result.appliedRange);
// 儀表板卡片才有累計占比
const cumulative = computed(() => (props.result as CardResult).cumulative ?? null);
const countText = (n: number | null | undefined) => (n ?? 0).toLocaleString(locale.value);
</script>

<template>
  <div class="column q-gutter-y-sm" data-testid="report-result">
    <div v-if="result.state !== 'ok' && result.state !== 'empty'" class="text-negative" :data-testid="`state-${result.state}`">
      {{ stateText }}
    </div>
    <template v-else>
      <div class="text-caption text-muted">
        <span v-if="range" data-testid="applied-range">{{ t('reports.appliedRange', { from: range.from, to: range.to }) }}</span>
        <span v-if="result.rowMeaning" class="q-ml-sm" data-testid="row-meaning">{{ t('reports.rowMeaning', { text: result.rowMeaning }) }}</span>
        <span v-if="result.basisIsFrozen" class="q-ml-sm" data-testid="basis-date">{{ t('reports.basisDate', { date: result.basisDate }) }}</span>
      </div>
      <div v-if="result.state === 'empty'" data-testid="state-empty">{{ stateText }}</div>
      <template v-else>
        <div v-if="showIndicator" class="row q-gutter-md stat-tiles" data-testid="report-indicator">
          <q-card v-for="m in measureCols" :key="m.key" flat bordered class="q-pa-md">
            <div class="text-caption">{{ m.label }}</div>
            <div class="text-stat">{{ fmt(result.total?.[m.key], m) }}</div>
          </q-card>
        </div>
        <template v-else-if="showChart">
          <ReportChart
            :viz="chartViz!" :groups="groups" :measures="measureCols" :locale="locale" :height="chartHeight ?? '360px'" :compact="chartHeight !== undefined"
            :cumulative="cumulative" :cumulative-label="t('dashboards.card.cumulative')"
            @pick="(i: number) => emit('drill', groups[i]!.groupKeys)"
          />
          <div v-if="result.total" class="row q-gutter-md text-weight-bold" data-testid="chart-total">
            <span v-for="m in measureCols" :key="m.key">{{ t('reports.total') }} {{ m.label }}: {{ fmt(result.total[m.key], m) }}</span>
          </div>
        </template>
        <q-markup-table v-else-if="showGroupTable" flat bordered dense data-testid="report-group-table">
          <thead>
            <tr>
              <th v-for="c in [...groupCols, ...measureCols]" :key="c.key" class="text-left">{{ c.label }}</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="(g, i) in groups" :key="i" :class="{ 'row-clickable': drillable }" :data-testid="`group-row-${i}`"
              @click="drillable && emit('drill', g.groupKeys)"
            >
              <td v-for="(c, ci) in groupCols" :key="c.key">{{ g.groupLabels[ci] }}</td>
              <td v-for="m in measureCols" :key="m.key">{{ fmt(g.values[m.key], m) }}</td>
            </tr>
            <tr v-if="result.total" class="text-weight-bold" data-testid="total-row">
              <td :colspan="Math.max(groupCols.length, 1)">{{ t('reports.total') }}</td>
              <td v-for="m in measureCols" :key="m.key">{{ fmt(result.total[m.key], m) }}</td>
            </tr>
          </tbody>
        </q-markup-table>
        <div v-if="result.detailTruncated" class="text-warning" data-testid="detail-truncated">
          {{ t('reports.detailTruncated', { shown: countText(result.detailRows?.length), total: countText(result.detailTotal) }) }}
        </div>
        <q-markup-table v-if="hasDetail" flat bordered dense data-testid="report-detail-table">
          <thead>
            <tr><th v-for="c in fieldCols" :key="c.key" class="text-left">{{ c.label }}</th></tr>
          </thead>
          <tbody>
            <tr v-for="(row, ri) in result.detailRows" :key="ri">
              <td v-for="(c, ci) in fieldCols" :key="c.key">{{ fmt(row[ci], c) }}</td>
            </tr>
          </tbody>
        </q-markup-table>
        <div v-if="hasDetail && !showIndicator && measureCols.length && result.total" class="row q-gutter-md text-weight-bold" data-testid="detail-total">
          <span v-for="m in measureCols" :key="m.key">{{ t('reports.total') }} {{ m.label }}: {{ fmt(result.total[m.key], m) }}</span>
        </div>
      </template>
      <div v-if="result.dedupedNote" class="text-caption text-warning" data-testid="deduped-note">{{ t('reports.dedupedNote') }}</div>
    </template>
  </div>
</template>
