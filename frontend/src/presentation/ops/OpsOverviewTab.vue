<script setup lang="ts">
import type { QTableColumn } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { formatTime, incidentTitle, labelOr, SEVERITY_ICON, statusLook } from 'src/domain/ops/ops-format';
import type { OpsOverview, ScheduledJobInfo } from 'src/domain/ops/ops-types';

// 總覽（維運設計 §17.3 第 1 項）：狀態卡一列、需要處理清單（伺服器給的前 10 件）、排程器表
const props = defineProps<{ overview: OpsOverview | undefined; loading: boolean; failed?: boolean }>();
const emit = defineEmits<{ openIncident: [id: number] }>();
const { t, te } = useI18n();

const writable = computed(() => props.overview?.checks.find((c) => c.code === 'database.writable'));
const channelState = computed(() => {
  const c = props.overview?.channel;
  if (!c) return '';
  if (!c.configured || !c.enabled) return t('ops.overview.channelOff');
  return c.consecutiveFailures > 0 ? t('ops.overview.channelFailing', { n: c.consecutiveFailures }) : t('ops.overview.channelOk');
});
const cards = computed(() => {
  const o = props.overview;
  if (!o) return [];
  return [
    { key: 'overall', label: t('ops.overview.overall'), value: labelOr(t, te, `ops.healthStatus.${o.overall}`, o.overall), look: statusLook(o.overall) },
    {
      key: 'writable', label: t('ops.overview.writable'),
      value: writable.value ? labelOr(t, te, `ops.healthStatus.${writable.value.status}`, writable.value.status) : '—',
      look: statusLook(writable.value?.status ?? 'unknown'),
    },
    {
      key: 'incidents', label: t('ops.overview.openIncidents'),
      value: t('ops.overview.incidentCounts', { critical: o.openIncidents.critical, warning: o.openIncidents.warning }),
      look: statusLook(o.openIncidents.critical ? 'critical' : o.openIncidents.warning ? 'warning' : 'ok'),
    },
    {
      key: 'channel', label: t('ops.overview.channel'), value: channelState.value,
      look: statusLook(!o.channel.configured || !o.channel.enabled ? 'unknown' : o.channel.consecutiveFailures ? 'warning' : 'ok'),
    },
    { key: 'version', label: t('ops.overview.version'), value: `${o.version}（${labelOr(t, te, `ops.edition.${o.edition}`, o.edition)}）`, look: { icon: 'info', color: 'primary' } },
    { key: 'database', label: t('ops.overview.database'), value: o.database, look: { icon: 'storage', color: 'primary' } },
  ];
});

const jobColumns = computed<QTableColumn<ScheduledJobInfo>[]>(() => [
  { name: 'jobKey', label: t('ops.jobs.job'), align: 'left', field: (j) => labelOr(t, te, `ops.jobs.names.${j.jobKey}`, j.jobKey) },
  // 排程器的狀態是 PascalCase（Succeeded、PartiallyFailed），鍵用小寫開頭
  { name: 'lastStatus', label: t('ops.jobs.lastStatus'), align: 'left', field: (j) => (j.lastStatus ? labelOr(t, te, `ops.jobs.status.${j.lastStatus.charAt(0).toLowerCase()}${j.lastStatus.slice(1)}`, j.lastStatus) : '—') },
  { name: 'lastFinished', label: t('ops.jobs.lastFinished'), align: 'left', field: (j) => formatTime(j.lastFinishedUtc) },
  { name: 'failures', label: t('ops.jobs.failures'), align: 'right', field: 'consecutiveFailures' },
  { name: 'nextDue', label: t('ops.jobs.nextDue'), align: 'left', field: (j) => formatTime(j.nextDueUtc) },
]);
</script>

<template>
  <div v-if="!overview && failed" class="text-negative" data-testid="ops-overview-failed">{{ t('ops.loadFailed') }}</div>
  <div v-else-if="!overview" class="text-muted">{{ loading ? t('designer.loading') : '' }}</div>
  <div v-else>
    <div class="row q-col-gutter-sm q-mb-md" data-testid="ops-cards">
      <div v-for="c in cards" :key="c.key" class="col-12 col-sm-6 col-md-4 col-xl-2">
        <q-card flat bordered class="full-height" :data-testid="`ops-card-${c.key}`">
          <q-card-section class="row no-wrap items-center q-gutter-sm">
            <q-icon :name="c.look.icon" :color="c.look.color" size="sm" />
            <div class="col" style="min-width: 0">
              <div class="text-caption text-muted">{{ c.label }}</div>
              <div class="text-subtitle2" style="overflow-wrap: anywhere">{{ c.value }}</div>
            </div>
          </q-card-section>
        </q-card>
      </div>
    </div>

    <div class="text-subtitle1 q-mb-xs">{{ t('ops.overview.needsAttention') }}</div>
    <q-list bordered separator class="q-mb-md" data-testid="ops-needs-attention">
      <q-item v-if="!overview.needsAttention.length"><q-item-section class="text-muted">{{ t('ops.overview.nothing') }}</q-item-section></q-item>
      <q-item
        v-for="i in overview.needsAttention" :key="i.id" clickable :data-testid="`ops-attention-${i.id}`" @click="emit('openIncident', i.id)"
      >
        <q-item-section avatar><q-icon :name="SEVERITY_ICON[i.severity].icon" :color="SEVERITY_ICON[i.severity].color" /></q-item-section>
        <q-item-section>
          <q-item-label style="overflow-wrap: anywhere">{{ incidentTitle(t, te, i.titleKey, i.titleParams) }}</q-item-label>
          <q-item-label caption>{{ t(`ops.category.${i.category}`) }} · {{ t(`ops.handler.${i.handler}`) }} · {{ formatTime(i.lastSeenUtc) }}</q-item-label>
        </q-item-section>
      </q-item>
    </q-list>

    <div class="text-subtitle1 q-mb-xs">{{ t('ops.jobs.title') }}</div>
    <q-table
      flat bordered row-key="jobKey" :rows="overview.jobs" :columns="jobColumns" :rows-per-page-options="[0]" hide-pagination
      :no-data-label="t('dynamic.noData')" :grid="$q.screen.lt.sm" data-testid="ops-jobs"
    >
      <template #item="{ cols }">
        <div class="col-12 q-py-xs">
          <q-card flat bordered>
            <q-list dense>
              <q-item v-for="c in cols" :key="c.name">
                <q-item-section>
                  <q-item-label caption>{{ c.label }}</q-item-label>
                  <q-item-label style="overflow-wrap: anywhere">{{ c.value }}</q-item-label>
                </q-item-section>
              </q-item>
            </q-list>
          </q-card>
        </div>
      </template>
    </q-table>
  </div>
</template>
