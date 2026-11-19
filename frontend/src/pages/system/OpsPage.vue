<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useOpsOverview } from 'src/application/dynamic/queries/ops-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import OpsChannelTab from 'src/presentation/ops/OpsChannelTab.vue';
import OpsHealthTab from 'src/presentation/ops/OpsHealthTab.vue';
import OpsIncidentsTab from 'src/presentation/ops/OpsIncidentsTab.vue';
import OpsOverviewTab from 'src/presentation/ops/OpsOverviewTab.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 維運監控頁（維運設計 §17.3）：四個分頁，URL query `tab` 記住；通知連結帶 `tab=incidents&incident=<id>` 直接開事件詳細
const TABS = ['overview', 'incidents', 'health', 'channel'] as const;
type Tab = (typeof TABS)[number];

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const overview = useOpsOverview();

const tab = computed<Tab>({
  get: () => (TABS as readonly string[]).includes(String(route.query.tab)) ? (route.query.tab as Tab) : 'overview',
  set: (v) => { void router.replace({ query: { ...route.query, tab: v, incident: undefined } }); },
});
const incidentId = computed(() => {
  const n = Number(route.query.incident);
  return Number.isInteger(n) && n > 0 ? n : null;
});
function openIncident(id: number | null) {
  void router.replace({ query: { ...route.query, tab: 'incidents', incident: id ?? undefined } });
}

const err = computed(() => overview.error.value as ApiError | null);
const forbidden = computed(() => isForbidden(err.value));
/** 資料庫連不上：所有需登入的 API 都回 503，整頁改成提示（§17.2.9）。 */
const unavailable = computed(() => err.value?.status === 503 && err.value.code === 'databaseUnavailable');
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <q-banner v-else-if="unavailable" class="bg-negative-soft" data-testid="ops-unavailable">
      <template #avatar><q-icon name="cloud_off" color="negative" /></template>
      {{ t('ops.unavailable') }}
    </q-banner>
    <template v-else>
      <div class="text-h6 q-mb-sm">{{ t('ops.title') }}</div>
      <q-banner v-if="overview.data.value?.memoryOnly" dense class="bg-info-soft q-mb-sm" data-testid="ops-memory-only">
        {{ t('ops.memoryOnly') }}
        <span v-if="overview.data.value.pendingDropped > 0" data-testid="ops-pending-dropped">
          {{ t('ops.pendingDropped', { n: overview.data.value.pendingDropped }) }}
        </span>
      </q-banner>
      <q-tabs v-model="tab" dense align="left" outside-arrows mobile-arrows class="q-mb-md" data-testid="ops-tabs">
        <q-tab v-for="name in TABS" :key="name" :name="name" :label="t(`ops.tabs.${name}`)" :data-testid="`ops-tab-${name}`" />
      </q-tabs>
      <q-tab-panels v-model="tab" animated keep-alive>
        <q-tab-panel name="overview" class="q-pa-none">
          <OpsOverviewTab :overview="overview.data.value" :loading="overview.isFetching.value" @open-incident="openIncident" />
        </q-tab-panel>
        <q-tab-panel name="incidents" class="q-pa-none">
          <OpsIncidentsTab :incident-id="incidentId" @open-incident="openIncident" />
        </q-tab-panel>
        <q-tab-panel name="health" class="q-pa-none">
          <OpsHealthTab />
        </q-tab-panel>
        <q-tab-panel name="channel" class="q-pa-none">
          <OpsChannelTab />
        </q-tab-panel>
      </q-tab-panels>
    </template>
  </q-page>
</template>
