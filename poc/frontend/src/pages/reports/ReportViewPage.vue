<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useCopyReport, useDeleteReport, useReport, useRunReport } from 'src/application/dynamic/queries/reports-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { ReportFilter } from 'src/domain/dynamic/report-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';
import DrillDialog from 'src/presentation/reports/DrillDialog.vue';
import ReportResultView from 'src/presentation/reports/ReportResultView.vue';
import ShareDialog from 'src/presentation/reports/ShareDialog.vue';

const { t, locale } = useI18n();
const route = useRoute();
const router = useRouter();
const id = computed(() => Number(route.params.id));
const report = useReport(id);
const run = useRunReport(id, locale);
const forbidden = computed(() => isForbidden(report.error.value));
const notFound = computed(() => (report.error.value as ApiError | null)?.status === 404);
const detail = computed(() => report.data.value);

// 條件：被遮蔽的只知道有條件，看不到內容
const filters = computed<ReportFilter[]>(() => [
  ...(detail.value?.definition.filters ?? []),
  ...(detail.value?.definition.measures.flatMap((m) => (m.condition ? [m.condition] : [])) ?? []),
]);
const opText = (op: string) => t(`dynamic.op.${op}`, op);
const filterText = (f: ReportFilter) => `${t('reports.fieldNo', { id: f.field.fieldId })} ${opText(f.op)} ${f.value === undefined || f.value === null ? '' : JSON.stringify(f.value)}`.trim();

const drillKeys = ref<string[] | null>(null);
const shareOpen = ref(false);

const exporting = ref(false);
const exportError = ref('');
async function exportXlsx() {
  exporting.value = true;
  exportError.value = '';
  try {
    const { blob, fileName } = await dynamicApi.exportReport(id.value, locale.value);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (e) {
    exportError.value = isForbidden(e) ? t('reports.exportForbidden') : t('reports.exportFailed');
  } finally {
    exporting.value = false;
  }
}

// 另存：400 是報表用到觀看者看不到的欄位，不是一般錯誤
const copy = useCopyReport();
const copyError = ref('');
function saveAsCopy() {
  copyError.value = '';
  copy.mutate(id.value, {
    onSuccess: (d) => { void router.push(`/reports/${d.id}`); },
    onError: (e) => { copyError.value = (e as unknown as ApiError).status === 400 ? t('reports.copyUnseeable') : t('reports.saveFailed'); },
  });
}

const confirmDelete = ref(false);
const remove = useDeleteReport();
const deleteError = ref('');
function doDelete() {
  deleteError.value = '';
  remove.mutate(id.value, {
    onSuccess: () => { confirmDelete.value = false; void router.push('/reports'); },
    onError: () => { deleteError.value = t('reports.saveFailed'); },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="notFound" data-testid="report-not-found">{{ t('reports.notFound') }}</div>
    <div v-else-if="report.isError.value" class="text-negative" data-testid="report-error">{{ t('reports.loadFailed') }}</div>
    <template v-else-if="detail">
      <div class="row items-center q-mb-md q-gutter-x-sm">
        <q-btn flat dense icon="arrow_back" to="/reports" :aria-label="t('reports.back')" />
        <div class="text-h6" data-testid="report-name">{{ detail.name }}</div>
        <span v-if="!detail.isOwner" class="text-caption text-grey-7" data-testid="report-owner">{{ t('reports.ownedBy', { name: detail.ownerName }) }}</span>
        <q-space />
        <q-btn v-if="detail.isOwner" outline dense no-caps :label="t('reports.edit')" :to="`/reports/${detail.id}/edit`" data-testid="report-edit" />
        <q-btn v-if="detail.isOwner" outline dense no-caps :label="t('reports.share')" data-testid="report-share" @click="shareOpen = true" />
        <q-btn
          v-if="!detail.isOwner" outline dense no-caps :label="t('reports.saveAsCopy')" :loading="copy.isPending.value"
          data-testid="report-copy" @click="saveAsCopy"
        />
        <q-btn outline dense no-caps icon="download" :label="t('reports.exportExcel')" :loading="exporting" data-testid="report-export" @click="exportXlsx" />
        <q-btn v-if="detail.isOwner" outline dense no-caps color="negative" :label="t('reports.delete')" data-testid="report-delete" @click="confirmDelete = true" />
      </div>
      <div v-if="copyError" class="text-negative q-mb-sm" data-testid="copy-error">{{ copyError }}</div>
      <div v-if="exportError" class="text-negative q-mb-sm" data-testid="export-error">{{ exportError }}</div>

      <q-expansion-item v-if="filters.length" dense :label="t('reports.conditions', { n: filters.length })" class="q-mb-md" data-testid="report-conditions">
        <ul class="q-my-sm">
          <li v-for="(f, i) in filters" :key="i" :data-testid="f.hidden ? 'condition-hidden' : 'condition'">
            {{ f.hidden ? t('reports.hiddenCondition') : filterText(f) }}
          </li>
        </ul>
      </q-expansion-item>

      <q-inner-loading :showing="run.isFetching.value" />
      <div v-if="run.isError.value" class="text-negative" data-testid="run-error">
        {{ t('reports.loadFailed') }}
        <q-btn flat dense color="primary" :label="t('reports.retry')" data-testid="run-retry" @click="run.refetch()" />
      </div>
      <ReportResultView v-else-if="run.data.value" :result="run.data.value" :viz="detail.definition.viz" @drill="drillKeys = $event" />

      <DrillDialog :report-id="detail.id" :group-keys="drillKeys" @close="drillKeys = null" />
      <ShareDialog :report-id="detail.id" :open="shareOpen" :group-ids="detail.sharedGroupIds" @close="shareOpen = false" />
      <q-dialog v-model="confirmDelete">
        <q-card style="width: 420px; max-width: 95vw">
          <q-card-section class="text-h6">{{ t('reports.deleteTitle', { name: detail.name }) }}</q-card-section>
          <q-card-section v-if="deleteError" class="text-negative">{{ deleteError }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('reports.cancel')" data-testid="delete-cancel" @click="confirmDelete = false" />
            <q-btn color="negative" :label="t('reports.delete')" :loading="remove.isPending.value" data-testid="delete-confirm" @click="doDelete" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
