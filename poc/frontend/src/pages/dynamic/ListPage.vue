<script setup lang="ts">
// POC uses a plain q-table; when merged into the main system, swap it for the framework's DataTable.
import { date, Notify, type QTableColumn } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { usePagedRecords, type RecordsState } from 'src/application/dynamic/queries/records-queries';
import { useModuleSchema } from 'src/application/dynamic/queries/schema-queries';
import {
  pickDefaultView, useCreateView, useDeleteView, useUpdateView, useViews,
} from 'src/application/dynamic/queries/views-queries';
import { pruneCondition, serializeFilter } from 'src/domain/dynamic/condition';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, DynamicRecord, I18nText, ListView, PagedResponse, ViewSort } from 'src/domain/dynamic/types';
import { referenceView } from 'src/presentation/dynamic/reference-display';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import ColumnChooser from 'src/presentation/dynamic/list/ColumnChooser.vue';
import ExportMenu from 'src/presentation/dynamic/list/ExportMenu.vue';
import FilterPanel from 'src/presentation/dynamic/list/FilterPanel.vue';
import ImportDialog from 'src/presentation/dynamic/list/ImportDialog.vue';
import SortEditor from 'src/presentation/dynamic/list/SortEditor.vue';
import ViewMenu from 'src/presentation/dynamic/list/ViewMenu.vue';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t, locale } = useI18n();
const route = useRoute();
const router = useRouter();
const moduleKey = computed(() => String(route.params.moduleKey));
const size = 20;

const page = ref(1);
const filter = ref<ConditionNode>();
const sort = ref<ViewSort[]>([]);
const q = ref('');
/** null = the schema's ShowInList fields. */
const columnIds = ref<number[] | null>(null);
const currentViewId = ref<number | null>(null);
/** Module the current state was initialised for; the records query only runs when it matches the route. */
const initializedFor = ref<string | null>(null);
const initialized = computed(() => initializedFor.value === moduleKey.value);
/** Last successful page, shown while the query is in error (placeholderData only covers pending). */
const lastGood = ref<PagedResponse<DynamicRecord>>();

const schemaQuery = useModuleSchema(moduleKey);
const viewsQuery = useViews(moduleKey);
const schema = computed(() => schemaQuery.data.value);
const views = computed(() => viewsQuery.data.value ?? []);
const currentView = computed(() => views.value.find((v) => v.id === currentViewId.value));

const state = computed<RecordsState>(() => ({
  page: page.value, size, filter: pruneCondition(filter.value), sort: sort.value, q: q.value.trim() || undefined,
}));
const exportQuery = computed(() => ({ filter: serializeFilter(filter.value), sort: sort.value, q: q.value.trim() || undefined }));
const importing = ref(false);
// 擴充模組的記錄掛在既有實體上，後端不支援匯入（決策 6）
const canImport = computed(() => !!schema.value?.permissions.add && schema.value.kind !== 'extension');
const recordsQuery = usePagedRecords(moduleKey, state, initialized);

const allFields = computed(() => schema.value?.blocks.flatMap((b) => b.fields) ?? []);
const defaultColumns = computed(() =>
  allFields.value.filter((f) => f.config.showInList && f.key !== schema.value?.titleFieldKey).map((f) => f.id));
const effectiveColumns = computed(() => {
  const ids = (columnIds.value ?? []).filter((id) => allFields.value.some((f) => f.id === id && f.key !== schema.value?.titleFieldKey));
  return ids.length ? columnIds.value! : defaultColumns.value;
});

function applyView(v?: ListView) {
  currentViewId.value = v?.id ?? null;
  filter.value = v?.filter;
  sort.value = v?.sort ?? [];
  columnIds.value = v ? v.columns : null;
  page.value = 1;
}
function resetAll() {
  applyView();
  q.value = '';
  initializedFor.value = null;
  lastGood.value = undefined;
}
watch(moduleKey, resetAll);
// Only initialise from data fetched after mount: cached views/schema may predate a designer change (stripped filters).
const fresh = computed(() => viewsQuery.isFetchedAfterMount.value && !viewsQuery.isFetching.value && !schemaQuery.isFetching.value);
watch([schema, fresh, moduleKey], () => {
  if (initialized.value || schema.value?.key !== moduleKey.value || !fresh.value) return;
  applyView(pickDefaultView(views.value));
  initializedFor.value = moduleKey.value;
}, { immediate: true });

const signature = (f: ConditionNode | undefined, s: ViewSort[], c: number[]) => JSON.stringify([pruneCondition(f) ?? null, s, c]);
const modified = computed(() => {
  const v = currentView.value;
  return !!v && signature(filter.value, sort.value, effectiveColumns.value) !== signature(v.filter, v.sort, v.columns);
});

watch(() => recordsQuery.data.value, (d) => {
  if (d && !recordsQuery.isPlaceholderData.value) lastGood.value = d;
});
const pageData = computed(() => recordsQuery.data.value ?? lastGood.value);

const notFound = computed(() => (schemaQuery.error.value as ApiError | null)?.status === 404);
const recordsError = computed(() => recordsQuery.error.value as ApiError | null);
const forbidden = computed(() => isForbidden(schemaQuery.error.value) || isForbidden(recordsError.value));
const failed = computed(() => !notFound.value && !forbidden.value
  && (!!schemaQuery.error.value || (!!recordsError.value && recordsError.value.status !== 400)));

function notifyApiError(e: unknown) {
  const err = e as ApiError | null;
  if (err?.status !== 400) return;
  const message = Object.values(err.errors ?? {}).flat().find((m) => !!m);
  if (message) Notify.create({ type: 'negative', message });
}
watch(recordsError, notifyApiError);
/** View writes: 403 = shared views need designer Edit (or reference fields the editor cannot see). */
function notifyViewError(e: unknown) {
  if (isForbidden(e)) Notify.create({ type: 'negative', message: t('dynamic.forbidden') });
  else notifyApiError(e);
}

const columns = computed<QTableColumn<DynamicRecord>[]>(() => {
  const s = schema.value;
  if (!s) return [];
  const title = s.titleFieldKey ? allFields.value.find((f) => f.key === s.titleFieldKey) : undefined;
  const listed = effectiveColumns.value
    .map((id) => allFields.value.find((f) => f.id === id))
    .filter((f): f is NonNullable<typeof f> => !!f && f.key !== s.titleFieldKey);
  return [
    ...(s.kind === 'extension' ? [{
      name: 'title', label: t('designer.extension.hostEntity'), align: 'left' as const, sortable: false,
      field: (r: DynamicRecord) => r.title,
    }] : []),
    ...(s.titleFieldKey ? [{
      name: title?.key ?? 'title', label: resolveI18n(title?.label, locale.value), align: 'left' as const, sortable: title?.config.isSortable,
      field: (r: DynamicRecord) => r.title,
    }] : []),
    ...listed.map<QTableColumn<DynamicRecord>>((f) => ({
      name: f.key, label: resolveI18n(f.label, locale.value), align: 'left', sortable: f.config.isSortable,
      field: (r) => (f.type === 'reference' && referenceView(r, f, t)?.text) || fieldTypeRegistry[f.type].format(r.values[f.key], f, locale.value),
    })),
    {
      name: 'modifiedTime', label: t('dynamic.modifiedTime'), align: 'left',
      field: (r) => date.formatDate(new Date(r.modifiedTime), 'YYYY-MM-DD HH:mm'),
    },
  ];
});

/** q-table shows a sort arrow only when exactly one column is sorted. */
const tableSort = computed(() => {
  const only = sort.value.length === 1 ? sort.value[0]! : undefined;
  const f = only && allFields.value.find((x) => x.id === only.field);
  return f ? { sortBy: f.key, descending: only.order === 'desc' } : { sortBy: null, descending: false };
});
const pagination = computed(() => ({
  page: page.value, rowsPerPage: size, rowsNumber: pageData.value?.totalCount ?? 0, ...tableSort.value,
}));

/** Header click replaces the sort with that single column; paging only moves the page. Idempotent, so safe for both events. */
function onPagination(p: { page?: number; sortBy?: string | null; descending?: boolean }) {
  const sortBy = p.sortBy ?? null;
  const descending = !!p.descending;
  if (sortBy !== tableSort.value.sortBy || (sortBy !== null && descending !== tableSort.value.descending)) {
    const f = allFields.value.find((x) => x.key === sortBy);
    sort.value = f ? [{ field: f.id, order: descending ? 'desc' : 'asc' }] : [];
    page.value = 1;
  } else {
    page.value = p.page ?? page.value;
  }
}

function setFilter(v: ConditionNode | undefined) { filter.value = v; page.value = 1; }
function setSort(v: ViewSort[]) { sort.value = v; page.value = 1; }
function setQ(v: string | number | null) { q.value = v == null ? '' : String(v); page.value = 1; }

const createView = useCreateView();
const updateView = useUpdateView();
const deleteView = useDeleteView();
const currentBody = (isDefault: boolean) => ({
  columns: effectiveColumns.value, filter: pruneCondition(filter.value) ?? null, sort: sort.value, isDefault,
});

function saveAs(v: { name: I18nText; shared: boolean; isDefault: boolean }) {
  createView.mutate({ moduleKey: moduleKey.value, body: { ...v, ...currentBody(v.isDefault) } }, {
    onSuccess: (created) => { currentViewId.value = created.id; },
    onError: notifyViewError,
  });
}
function updateCurrent() {
  const v = currentView.value;
  if (v) updateView.mutate({ moduleKey: moduleKey.value, id: v.id, body: { name: v.name, ...currentBody(v.isDefault) } }, { onError: notifyViewError });
}
function setDefault() {
  const v = currentView.value;
  if (!v) return;
  const body = { name: v.name, columns: v.columns, filter: v.filter ?? null, sort: v.sort, isDefault: true };
  updateView.mutate({ moduleKey: moduleKey.value, id: v.id, body }, { onError: notifyViewError });
}
function removeCurrent() {
  const v = currentView.value;
  if (!v) return;
  deleteView.mutate({ moduleKey: moduleKey.value, id: v.id }, {
    onSuccess: () => applyView(),
    onError: notifyViewError,
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="notFound" class="text-negative">{{ t('dynamic.moduleNotFound') }}</div>
    <div v-else-if="failed" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <template v-else-if="schema">
      <!-- 標題、檢視、搜尋與新增／匯入／匯出併成一列，窄螢幕自動換行 -->
      <div class="row items-center q-mb-sm q-gutter-sm">
        <div class="text-h6">{{ resolveI18n(schema.name, locale) }}</div>
        <ViewMenu
          :views="views" :current="currentView" :modified="modified"
          @select="(id) => applyView(views.find((v) => v.id === id))"
          @save-as="saveAs" @update="updateCurrent" @remove="removeCurrent" @set-default="setDefault"
        />
        <q-input
          :model-value="q" :placeholder="t('dynamic.view.search')" maxlength="100" debounce="300" clearable outlined dense
          data-testid="keyword" style="min-width: 14rem" @update:model-value="setQ"
        >
          <template #prepend><q-icon name="search" /></template>
        </q-input>
        <q-space />
        <q-btn v-if="schema.permissions.add" color="primary" icon="add" data-testid="new-btn" :label="t('dynamic.new')" :to="`/dynamic/${moduleKey}/new`" />
        <q-btn v-if="canImport" outline icon="upload" data-testid="import-btn" :label="t('dynamic.import.title')" @click="importing = true" />
        <ExportMenu :schema="schema" :query="exportQuery" />
      </div>
      <div v-if="schema.readOnly" class="row items-center no-wrap q-mb-sm text-muted" data-testid="closed-banner">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('dynamic.closedBanner') }}</span>
      </div>
      <div v-if="currentView?.staleFieldIds.length" class="text-warning q-mb-sm" data-testid="stale-notice">
        {{ t('dynamic.view.stale', { n: currentView.staleFieldIds.length }) }}
      </div>
      <FilterPanel :schema="schema" :model-value="filter" class="q-mb-sm" @update:model-value="setFilter" />
      <q-expansion-item dense icon="sort" :label="t('dynamic.sort.title')" class="q-mb-sm">
        <SortEditor :schema="schema" :model-value="sort" class="q-pa-sm" @update:model-value="setSort" />
      </q-expansion-item>
      <q-expansion-item dense icon="view_column" :label="t('dynamic.columns.title')" class="q-mb-sm">
        <ColumnChooser :schema="schema" :model-value="effectiveColumns" @update:model-value="(v) => (columnIds = v)" />
      </q-expansion-item>
      <q-table
        flat bordered row-key="id" :rows="pageData?.items ?? []" :columns="columns"
        :loading="recordsQuery.isFetching.value" :pagination="pagination" :rows-per-page-options="[20]"
        :no-data-label="t('dynamic.noData')"
        @request="(p) => onPagination(p.pagination)"
        @update:pagination="onPagination"
        @row-click="(_e, row) => router.push(`/dynamic/${moduleKey}/${row.id}`)"
      />
      <ImportDialog v-if="canImport" v-model="importing" :schema="schema" />
    </template>
  </q-page>
</template>
