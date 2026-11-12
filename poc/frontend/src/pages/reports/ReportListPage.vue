<script setup lang="ts">
import type { QTableColumn } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useReports } from 'src/application/dynamic/queries/reports-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { ReportItem } from 'src/domain/dynamic/report-types';
import { isForbidden } from 'src/services/api/http';

const { t } = useI18n();
const router = useRouter();
const reports = useReports();
const forbidden = computed(() => isForbidden(reports.error.value));
const columns = computed<QTableColumn<ReportItem>[]>(() => [
  { name: 'name', label: t('reports.name'), align: 'left', field: 'name' },
  { name: 'owner', label: t('reports.owner'), align: 'left', field: (r) => (r.isOwner ? t('reports.mine') : r.ownerName) },
]);
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('reports.myReports') }}</div>
        <q-space />
        <q-btn color="primary" icon="add" :label="t('reports.create')" to="/reports/new" data-testid="report-create" />
      </div>
      <div v-if="reports.isError.value" class="text-negative" data-testid="reports-error">
        {{ t('reports.loadFailed') }}
        <q-btn flat dense color="primary" :label="t('reports.retry')" data-testid="reports-retry" @click="reports.refetch()" />
      </div>
      <q-table
        v-else flat bordered row-key="id" :rows="reports.data.value ?? []" :columns="columns" :loading="reports.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination :no-data-label="t('reports.empty')"
      >
        <template #body="{ row, cols }">
          <q-tr class="row-clickable row-chevron" :data-testid="`report-row-${row.id}`" @click="router.push(`/reports/${row.id}`)">
            <q-td v-for="c in cols" :key="c.name" :align="c.align">
              {{ c.value }}
              <q-badge v-if="c.name === 'name' && row.isOwner && row.sharedGroupIds.length" class="q-ml-sm" color="muted" :label="t('reports.shared')" />
            </q-td>
          </q-tr>
        </template>
      </q-table>
    </template>
  </q-page>
</template>
