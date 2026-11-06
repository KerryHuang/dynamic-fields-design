<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { DEFAULT_CARD_OPTIONS, type CardOptions, type DashboardCard } from 'src/domain/dynamic/dashboard-types';
import type { ReportColumn, ReportViz } from 'src/domain/dynamic/report-types';

/**
 * 卡片設定：標題、呈現方式與 spec §4 的卡片選項。排序依據只能選這張卡片目前查得到的欄（columns），
 * 查不到（無權限、載入失敗，或在管理畫面沒有預覽）時不提供排序。
 */
const props = defineProps<{
  card: DashboardCard | null; columns: ReportColumn[]; reportName?: string;
  /** 可連結的儀表板（觀看者看得到的）。 */
  dashboards: { id: number; name: string }[];
}>();
const emit = defineEmits<{ close: []; save: [change: { title: string | null; viz: ReportViz; options: CardOptions }] }>();
const { t } = useI18n();

const VIZS: ReportViz[] = ['table', 'indicator', 'line', 'bar', 'donut'];
const RANGES = ['today', 'thisWeek', 'lastWeek', 'thisMonth', 'lastMonth', 'thisYear', 'lastYear', 'lastDays', 'lastWeeks', 'lastMonths'];

const title = ref('');
const viz = ref<ReportViz>('bar');
const rangeKind = ref<string | null>(null);
const rangeN = ref<number | null>(30);
const topN = ref<number | null>(null);
const sortKey = ref<string | null>(null);
const sortDesc = ref(true);
const cumulative = ref(false);
const comparePeriods = ref<number | null>(null);
const link = ref<number | null>(null);

watch(() => props.card, (c) => {
  if (!c) return;
  title.value = c.title ?? '';
  viz.value = c.viz;
  rangeKind.value = c.options.range?.kind ?? null;
  rangeN.value = c.options.range?.n ?? 30;
  topN.value = c.options.topN ?? null;
  sortKey.value = c.options.sortKey ?? null;
  sortDesc.value = c.options.sortDesc;
  cumulative.value = c.options.cumulative;
  comparePeriods.value = c.options.comparePeriods ?? null;
  link.value = c.options.linkDashboardId ?? null;
}, { immediate: true });

const sortable = computed(() => props.columns.filter((c) => c.role === 'group' || c.role === 'measure'));
const needsN = computed(() => rangeKind.value !== null && rangeKind.value.startsWith('last') && rangeKind.value !== 'lastWeek' && rangeKind.value !== 'lastMonth' && rangeKind.value !== 'lastYear');
const intOrNull = (v: number | null) => (v === null || v === undefined || Number.isNaN(Number(v)) ? null : Math.trunc(Number(v)));

function submit() {
  const options: CardOptions = {
    ...DEFAULT_CARD_OPTIONS,
    range: rangeKind.value ? { kind: rangeKind.value, n: needsN.value ? intOrNull(rangeN.value) : null } : null,
    topN: intOrNull(topN.value),
    sortKey: sortKey.value, sortDesc: sortDesc.value,
    cumulative: viz.value === 'bar' && cumulative.value,
    comparePeriods: viz.value === 'indicator' ? intOrNull(comparePeriods.value) : null,
    linkDashboardId: link.value,
  };
  emit('save', { title: title.value.trim() || null, viz: viz.value, options });
}
</script>

<template>
  <q-dialog :model-value="card !== null" @update:model-value="emit('close')">
    <q-card v-if="card" style="width: 520px; max-width: 95vw">
      <q-card-section class="text-h6">{{ t('dashboards.options.title') }}</q-card-section>
      <q-card-section class="column q-gutter-y-sm" style="max-height: 70vh; overflow: auto">
        <q-input v-model="title" outlined dense maxlength="100" :label="t('dashboards.options.cardTitle')" :placeholder="reportName" data-testid="opt-title" />
        <q-select
          v-model="viz" :options="VIZS" :option-label="(v: string) => t(`reports.designer.vizName.${v}`)" emit-value map-options
          outlined dense :label="t('dashboards.options.viz')" data-testid="opt-viz"
        />
        <q-select
          v-model="rangeKind" :options="RANGES" :option-label="(v: string) => t(`reports.designer.range.${v}`)" emit-value map-options clearable
          outlined dense :label="t('dashboards.options.range')" :hint="t('dashboards.options.rangeHint')" data-testid="opt-range"
        />
        <q-input v-if="needsN" v-model.number="rangeN" type="number" min="1" outlined dense :label="t('reports.designer.rangeN')" data-testid="opt-range-n" />
        <q-input v-model.number="topN" type="number" min="1" max="100" outlined dense clearable :label="t('dashboards.options.topN')" :hint="t('dashboards.options.topNHint')" data-testid="opt-topn" />
        <template v-if="sortable.length">
          <q-select
            v-model="sortKey" :options="sortable" option-value="key" option-label="label" emit-value map-options clearable
            outlined dense :label="t('dashboards.options.sort')" data-testid="opt-sort"
          />
          <q-toggle v-if="sortKey" v-model="sortDesc" dense :label="t('dashboards.options.sortDesc')" data-testid="opt-sort-desc" />
        </template>
        <q-toggle v-if="viz === 'bar'" v-model="cumulative" dense :label="t('dashboards.options.cumulative')" data-testid="opt-cumulative" />
        <q-input
          v-if="viz === 'indicator'" v-model.number="comparePeriods" type="number" min="1" max="12" outlined dense clearable
          :label="t('dashboards.options.compare')" :hint="t('dashboards.options.compareHint')" data-testid="opt-compare"
        />
        <q-select
          v-model="link" :options="dashboards" option-value="id" option-label="name" emit-value map-options clearable
          outlined dense :label="t('dashboards.options.link')" data-testid="opt-link"
        />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('reports.cancel')" data-testid="opt-cancel" @click="emit('close')" />
        <q-btn color="primary" :label="t('reports.save')" data-testid="opt-save" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
