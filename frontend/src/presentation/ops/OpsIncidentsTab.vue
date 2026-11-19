<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAcknowledgeIncident, useIncidents, useResolveIncident } from 'src/application/dynamic/queries/ops-queries';
import RowActionMenu from 'src/components/framework/RowActionMenu.vue';
import { formatTime, incidentTitle, SEVERITY_ICON } from 'src/domain/ops/ops-format';
import type { IncidentCategory, IncidentFilter, IncidentSeverity, IncidentStatus, IncidentSummary } from 'src/domain/ops/ops-types';
import type { ApiError } from 'src/services/api/http';
import IncidentDetailDialog from './IncidentDetailDialog.vue';

// 事件分頁（維運設計 §17.3 第 2 項）：篩選＋清單（keyset「載入更多」）；手機變卡片、動作收進 ⋮；點開是詳細
const props = defineProps<{ incidentId: number | null }>();
const emit = defineEmits<{ openIncident: [id: number | null] }>();
const { t, te } = useI18n();
const $q = useQuasar();

const STATUSES: IncidentStatus[] = ['open', 'acknowledged', 'resolved'];
const CATEGORIES: IncidentCategory[] = ['config', 'defect', 'infra', 'security'];
const SEVERITIES: IncidentSeverity[] = ['critical', 'warning', 'info'];
const filter = ref<IncidentFilter>({ status: ['open', 'acknowledged'], category: null, severity: null });
const query = useIncidents(filter);
const rows = computed(() => query.data.value?.pages.flatMap((p) => p.items) ?? []);
const showFilters = ref(false);

const statusOptions = computed(() => STATUSES.map((s) => ({ value: s, label: t(`ops.status.${s}`) })));
const categoryOptions = computed(() => CATEGORIES.map((c) => ({ value: c, label: t(`ops.category.${c}`) })));
const severityOptions = computed(() => SEVERITIES.map((s) => ({ value: s, label: t(`ops.severity.${s}`) })));

const title = (i: IncidentSummary) => incidentTitle(t, te, i.titleKey, i.titleParams);
const columns = computed<QTableColumn<IncidentSummary>[]>(() => [
  { name: 'severity', label: t('ops.incidents.severity'), align: 'left', field: (i) => t(`ops.severity.${i.severity}`) },
  { name: 'title', label: t('ops.incidents.titleCol'), align: 'left', field: title },
  { name: 'category', label: t('ops.incidents.category'), align: 'left', field: (i) => `${t(`ops.category.${i.category}`)}／${t(`ops.handler.${i.handler}`)}` },
  { name: 'status', label: t('ops.incidents.status'), align: 'left', field: (i) => t(`ops.status.${i.status}`) },
  { name: 'count', label: t('ops.incidents.count'), align: 'right', field: 'occurrenceCount' },
  { name: 'lastSeen', label: t('ops.incidents.lastSeen'), align: 'left', field: (i) => formatTime(i.lastSeenUtc) },
  { name: 'actions', label: '', align: 'right', field: 'id' },
]);
/** 平板只留主要欄位（§17.3 版型表）；桌機全欄位。 */
const visibleColumns = computed(() => ($q.screen.md || $q.screen.gt.md ? columns.value.map((c) => c.name) : ['severity', 'title', 'status', 'actions']));

const ack = useAcknowledgeIncident();
const resolve = useResolveIncident();
const resolving = ref<IncidentSummary | null>(null);
const resolveNote = ref('');
function failed(e: unknown) {
  const err = e as ApiError;
  // 別人先按了：提示後清單已經重新整理（mutation onSettled）
  const message = err.code === 'incidentStateChanged' ? t('ops.incidents.stateChanged') : err.message || t('ops.saveFailed');
  $q.notify({ type: 'negative', message });
}
function acknowledge(i: IncidentSummary) {
  ack.mutate({ id: i.id, expectedStatus: i.status }, { onSuccess: () => $q.notify({ type: 'positive', message: t('ops.incidents.acknowledged') }), onError: failed });
}
function openResolve(i: IncidentSummary) {
  resolving.value = i;
  resolveNote.value = '';
}
function confirmResolve() {
  const i = resolving.value;
  if (!i) return;
  resolve.mutate({ id: i.id, expectedStatus: i.status, note: resolveNote.value.trim() }, {
    onSuccess: () => { resolving.value = null; $q.notify({ type: 'positive', message: t('ops.incidents.resolved') }); },
    onError: (e) => { resolving.value = null; failed(e); },
  });
}
const actionsOf = (i: IncidentSummary) => [
  ...(i.status === 'open' ? [{ label: t('ops.incidents.acknowledge'), run: () => acknowledge(i), testid: `incident-ack-${i.id}` }] : []),
  ...(i.manualResolvable && i.status !== 'resolved'
    ? [{ label: t('ops.incidents.resolve'), run: () => openResolve(i), testid: `incident-resolve-${i.id}` }] : []),
];
</script>

<template>
  <div>
    <q-expansion-item
      v-if="$q.screen.lt.sm" v-model="showFilters" dense icon="filter_list" :label="t('ops.filters')" class="q-mb-sm" data-testid="incident-filter-toggle"
    >
      <div class="column no-wrap q-gutter-y-sm q-pa-sm">
        <q-select v-model="filter.status" :options="statusOptions" multiple use-chips emit-value map-options dense outlined :label="t('ops.incidents.status')" />
        <q-select v-model="filter.category" :options="categoryOptions" clearable emit-value map-options dense outlined :label="t('ops.incidents.category')" />
        <q-select v-model="filter.severity" :options="severityOptions" clearable emit-value map-options dense outlined :label="t('ops.incidents.severity')" />
      </div>
    </q-expansion-item>
    <div v-else class="row q-col-gutter-sm q-mb-sm" data-testid="incident-filters">
      <div class="col-12 col-sm-4">
        <q-select
          v-model="filter.status" :options="statusOptions" multiple use-chips emit-value map-options dense outlined :label="t('ops.incidents.status')"
          data-testid="incident-filter-status"
        />
      </div>
      <div class="col-12 col-sm-4">
        <q-select
          v-model="filter.category" :options="categoryOptions" clearable emit-value map-options dense outlined :label="t('ops.incidents.category')"
          data-testid="incident-filter-category"
        />
      </div>
      <div class="col-12 col-sm-4">
        <q-select
          v-model="filter.severity" :options="severityOptions" clearable emit-value map-options dense outlined :label="t('ops.incidents.severity')"
          data-testid="incident-filter-severity"
        />
      </div>
    </div>

    <q-banner v-if="query.isError.value" dense class="bg-negative-soft q-mb-sm" data-testid="incident-load-failed">{{ t('ops.loadFailed') }}</q-banner>
    <q-table
      flat bordered row-key="id" :rows="rows" :columns="columns" :visible-columns="visibleColumns" :loading="query.isFetching.value"
      :rows-per-page-options="[0]" hide-pagination :no-data-label="query.isError.value ? t('ops.loadFailed') : t('ops.incidents.empty')"
      :grid="$q.screen.lt.sm" data-testid="incident-table"
    >
      <template #body="{ row, cols }">
        <q-tr class="row-clickable" tabindex="0" :data-testid="`incident-row-${row.id}`" @click="emit('openIncident', row.id)" @keydown.enter.self.prevent="emit('openIncident', row.id)">
          <template v-for="c in cols" :key="c.name">
            <q-td v-if="c.name === 'severity'">
              <q-icon :name="SEVERITY_ICON[row.severity as IncidentSeverity].icon" :color="SEVERITY_ICON[row.severity as IncidentSeverity].color" size="xs" class="q-mr-xs" />
              {{ c.value }}
            </q-td>
            <q-td v-else-if="c.name === 'title'" style="white-space: normal; overflow-wrap: anywhere">
              {{ c.value }}
              <q-badge v-if="row.regression" color="negative" class="q-ml-xs">{{ t('ops.incidents.regression') }}</q-badge>
            </q-td>
            <q-td v-else-if="c.name === 'actions'" :align="c.align" @click.stop>
              <q-btn
                v-for="a in actionsOf(row)" :key="a.testid" flat dense no-caps color="primary" :label="a.label" :data-testid="a.testid" @click="a.run()"
              />
            </q-td>
            <q-td v-else :align="c.align">{{ c.value }}</q-td>
          </template>
        </q-tr>
      </template>
      <template #item="{ row }">
        <div class="col-12 q-py-xs">
          <q-card
            flat bordered class="cursor-pointer card-clickable" tabindex="0" :data-testid="`incident-card-${row.id}`"
            @click="emit('openIncident', row.id)" @keydown.enter.self.prevent="emit('openIncident', row.id)"
          >
            <q-card-section class="row no-wrap items-start q-gutter-sm">
              <q-icon :name="SEVERITY_ICON[row.severity as IncidentSeverity].icon" :color="SEVERITY_ICON[row.severity as IncidentSeverity].color" size="sm" />
              <div class="col" style="min-width: 0">
                <div style="overflow-wrap: anywhere">
                  {{ title(row) }}
                  <q-badge v-if="row.regression" color="negative" class="q-ml-xs">{{ t('ops.incidents.regression') }}</q-badge>
                </div>
                <div class="text-caption text-muted">
                  {{ t(`ops.status.${row.status}`) }} · {{ t(`ops.category.${row.category}`) }} · {{ t('ops.incidents.countN', { n: row.occurrenceCount }) }}
                  · {{ formatTime(row.lastSeenUtc) }}
                </div>
              </div>
              <RowActionMenu v-if="actionsOf(row).length" :testid="`incident-more-${row.id}`" :actions="actionsOf(row)" />
            </q-card-section>
          </q-card>
        </div>
      </template>
    </q-table>
    <div v-if="query.hasNextPage.value" class="row justify-center q-mt-sm">
      <q-btn flat color="primary" :label="t('ops.loadMore')" :loading="query.isFetchingNextPage.value" data-testid="incident-more" @click="query.fetchNextPage()" />
    </div>

    <IncidentDetailDialog :incident-id="props.incidentId" @close="emit('openIncident', null)" @acknowledge="acknowledge" @resolve="openResolve" />

    <q-dialog :model-value="!!resolving" @update:model-value="(v) => { if (!v) resolving = null; }">
      <q-card class="ops-confirm">
        <q-card-section class="text-subtitle1">{{ t('ops.incidents.resolveTitle') }}</q-card-section>
        <q-card-section>
          <q-input v-model="resolveNote" type="textarea" autogrow dense outlined :label="t('ops.incidents.resolveNote')" data-testid="incident-resolve-note" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('designer.close')" @click="resolving = null" />
          <q-btn color="primary" :label="t('ops.incidents.resolve')" :loading="resolve.isPending.value" data-testid="incident-resolve-confirm" @click="confirmResolve" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.ops-confirm { width: 420px; max-width: 95vw; }
</style>
