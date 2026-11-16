<script setup lang="ts">
import { ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useReports } from 'src/application/dynamic/queries/reports-queries';
import type { ReportViz } from 'src/domain/dynamic/report-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** 把一張看得到的報表放上儀表板，呈現方式預設跟報表一樣，之後屬於卡片。 */
const props = defineProps<{ open: boolean }>();
const emit = defineEmits<{ close: []; add: [reportId: number, viz: ReportViz] }>();
const { t } = useI18n();
const reports = useReports();
const reportId = ref<number | null>(null);
const viz = ref<ReportViz>('bar');
const VIZS: ReportViz[] = ['table', 'indicator', 'line', 'bar', 'donut'];

watch(() => props.open, (o) => { if (o) { reportId.value = null; viz.value = 'bar'; } });
async function pick(id: number) {
  reportId.value = id;
  try { viz.value = (await dynamicApi.getReport(id)).definition.viz; } catch { /* 預設呈現方式只是便利，取不到就維持目前的選擇 */ }
}
</script>

<template>
  <q-dialog :model-value="open" @update:model-value="emit('close')">
    <q-card style="width: 520px; max-width: 95vw">
      <q-card-section class="text-h6">{{ t('dashboards.add.title') }}</q-card-section>
      <q-card-section style="max-height: 55vh; overflow: auto">
        <div v-if="reports.isError.value" class="text-negative" data-testid="add-load-error">{{ t('reports.loadFailed') }}</div>
        <div v-else-if="reports.data.value && reports.data.value.length === 0" class="text-muted" data-testid="add-empty">{{ t('dashboards.add.noReports') }}</div>
        <q-list v-else separator>
          <q-item
            v-for="r in reports.data.value ?? []" :key="r.id" tag="label" clickable :active="reportId === r.id"
            :data-testid="`add-report-${r.id}`" @click="pick(r.id)"
          >
            <q-item-section avatar><q-radio :model-value="reportId" :val="r.id" dense @update:model-value="pick(r.id)" /></q-item-section>
            <q-item-section>
              <q-item-label>{{ r.name }}</q-item-label>
              <q-item-label caption>{{ r.isOwner ? t('reports.mine') : r.ownerName }}</q-item-label>
            </q-item-section>
          </q-item>
        </q-list>
      </q-card-section>
      <q-card-section v-if="reportId !== null">
        <q-select
          v-model="viz" :options="VIZS" :option-label="(v: string) => t(`reports.designer.vizName.${v}`)" emit-value map-options
          outlined dense :label="t('dashboards.options.viz')" data-testid="add-viz"
        />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('reports.cancel')" data-testid="add-cancel" @click="emit('close')" />
        <q-btn color="primary" :label="t('dashboards.add.confirm')" :disable="reportId === null" data-testid="add-confirm" @click="emit('add', reportId!, viz)" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
