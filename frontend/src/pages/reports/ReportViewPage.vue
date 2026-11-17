<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useCopyReport, useDeleteReport, useReport, useRunReport, useSourceCatalog } from 'src/application/dynamic/queries/reports-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { NO_RUN_FILTER, type ReportFilter, type ReportRunFilter } from 'src/domain/dynamic/report-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';
import TaskDialog from 'src/presentation/dynamic/tasks/TaskDialog.vue';
import DrillDialog from 'src/presentation/reports/DrillDialog.vue';
import ReportFilterBar from 'src/presentation/reports/ReportFilterBar.vue';
import ReportResultView from 'src/presentation/reports/ReportResultView.vue';
import { findEntry, listFields, valueText } from 'src/presentation/reports/report-catalog';
import ShareDialog from 'src/presentation/reports/ShareDialog.vue';

const { t, locale } = useI18n();
const route = useRoute();
const router = useRouter();
const id = computed(() => Number(route.params.id));
const report = useReport(id);
// 暫時篩選（期間、部門）只存頁面狀態，不進網址、不存檔；換報表就重置（看板設計 §8.2）
const runFilter = ref<ReportRunFilter>({ ...NO_RUN_FILTER });
watch(id, () => { runFilter.value = { ...NO_RUN_FILTER }; drillKeys.value = null; openTaskId.value = null; });
const run = useRunReport(id, locale, runFilter);
const forbidden = computed(() => isForbidden(report.error.value));
const notFound = computed(() => (report.error.value as ApiError | null)?.status === 404);
const detail = computed(() => report.data.value);
const me = useMe();
// 系統管理員可刪所有共用報表（spec §7）；編輯與分享仍限擁有者
const canDelete = computed(() => !!detail.value && (detail.value.isOwner || !!me.data.value?.isSystemAdmin));

// 條件：被遮蔽的只知道有條件，看不到內容
const filters = computed<ReportFilter[]>(() => [
  ...(detail.value?.definition.filters ?? []),
  ...(detail.value?.definition.measures.flatMap((m) => (m.condition ? [m.condition] : [])) ?? []),
]);
const opText = (op: string) => t(`dynamic.op.${op}`, op);
// 欄位名稱與選項標籤取自欄位目錄；目錄沒有的欄位（被刪、不可讀）退回 #id
const catalog = useSourceCatalog(computed(() => detail.value?.definition.dataset), computed(() => detail.value?.definition.mainModuleKey), locale);
// 部門篩選只有任務資料來源有；期間篩選要報表有主要日期欄位才作用
const isTaskSource = computed(() => !!detail.value?.definition.dataset && detail.value.definition.dataset !== 'module');
const rangeAvailable = computed(() => !!detail.value?.definition.primaryDateField);
const entries = computed(() => (catalog.data.value ? listFields(catalog.data.value, detail.value?.definition.child, t('reports.titleField')) : []));
const filterText = (f: ReportFilter) => {
  const e = findEntry(entries.value, f.field);
  return `${e ? e.label : t('reports.fieldNo', { id: f.field.fieldId })} ${opText(f.op)} ${valueText(f.value, e?.field, t('reports.inactiveMark'))}`.trim();
};

const drillKeys = ref<string[] | null>(null);
// 點明細列（任務資料來源）在原頁開任務詳細；下鑽清單留在底下，關掉就回到它。任務改過數字會變，關閉時重算
const openTaskId = ref<number | null>(null);
const qc = useQueryClient();
function setOpenTask(v: number | null) {
  openTaskId.value = v;
  if (v === null) void Promise.all([qc.invalidateQueries({ queryKey: ['report-run'] }), qc.invalidateQueries({ queryKey: ['report-drill'] })]);
}
const shareOpen = ref(false);

const exporting = ref(false);
const exportError = ref('');
async function exportXlsx() {
  exporting.value = true;
  exportError.value = '';
  try {
    const { blob, fileName } = await dynamicApi.exportReport(id.value, locale.value, runFilter.value);
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
    onError: (e) => { copyError.value = [400, 403].includes((e as unknown as ApiError).status) ? t('reports.copyUnseeable') : t('reports.saveFailed'); },
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
        <span v-if="!detail.isOwner" class="text-caption text-muted" data-testid="report-owner">{{ t('reports.ownedBy', { name: detail.ownerName }) }}</span>
        <q-space />
        <q-btn v-if="detail.isOwner" outline dense no-caps :label="t('reports.edit')" :to="`/reports/${detail.id}/edit`" data-testid="report-edit" />
        <q-btn v-if="detail.isOwner" outline dense no-caps :label="t('reports.share')" data-testid="report-share" @click="shareOpen = true" />
        <q-btn
          v-if="!detail.isOwner" outline dense no-caps :label="t('reports.saveAsCopy')" :loading="copy.isPending.value"
          data-testid="report-copy" @click="saveAsCopy"
        />
        <span>
          <q-btn
            outline dense no-caps icon="download" :label="t('reports.exportExcel')" :loading="exporting"
            :disable="!detail.canExport" data-testid="report-export" @click="exportXlsx"
          />
          <q-tooltip v-if="!detail.canExport">{{ t('reports.exportNoPermission') }}</q-tooltip>
        </span>
        <q-btn v-if="canDelete" outline dense no-caps color="negative" :label="t('reports.delete')" data-testid="report-delete" @click="confirmDelete = true" />
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

      <ReportFilterBar v-model="runFilter" :show-department="isTaskSource" :range-available="rangeAvailable" />

      <q-inner-loading :showing="run.isFetching.value" />
      <div v-if="run.isError.value" class="text-negative" data-testid="run-error">
        {{ t('reports.loadFailed') }}
        <q-btn flat dense color="primary" :label="t('reports.retry')" data-testid="run-retry" @click="run.refetch()" />
      </div>
      <ReportResultView
        v-else-if="run.data.value" drillable openable-tasks :result="run.data.value" :viz="detail.definition.viz" @drill="drillKeys = $event"
        @open-task="setOpenTask"
      />

      <DrillDialog
        :report-id="detail.id" :group-keys="drillKeys" :filter="runFilter" openable-tasks @close="drillKeys = null" @open-task="setOpenTask"
      />
      <TaskDialog :task-id="openTaskId" :unavailable-text="t('reports.drillTaskUnavailable')" @update:task-id="setOpenTask" />
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
