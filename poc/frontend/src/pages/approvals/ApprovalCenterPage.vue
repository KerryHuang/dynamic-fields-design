<script setup lang="ts">
import { date, useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter, type LocationQueryRaw } from 'vue-router';
import { useApprovalAllAccess } from 'src/application/dynamic/queries/approval-center-queries';
import { useApprovalInbox } from 'src/application/dynamic/queries/approval-queries';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import type { ApprovalBox, ApprovalInboxFilter, ApprovalInboxItem, ApprovalInstanceStatus } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { isForbidden } from 'src/services/api/http';
import ApprovalInboxFilters from 'src/presentation/approvals/ApprovalInboxFilters.vue';
import DelegateBanner from 'src/presentation/approvals/DelegateBanner.vue';
import DelegateSettingsDialog from 'src/presentation/approvals/DelegateSettingsDialog.vue';
import { INBOX_STATUSES } from 'src/presentation/approvals/inbox-filter';
import { instanceTitle } from 'src/presentation/dynamic/approval/approval-display';

const BOXES: ApprovalBox[] = ['todo', 'done', 'submitted'];
const SIZE = 20;
const DAY = /^\d{4}-\d{2}-\d{2}$/;
const isDay = (v: string) => DAY.test(v) && !Number.isNaN(Date.parse(v));

const { t, locale } = useI18n();
const route = useRoute();
const router = useRouter();
const $q = useQuasar();

const all = useApprovalAllAccess();
const wantedTab = computed<ApprovalBox>(() => {
  const v = String(route.query.tab);
  return v === 'all' || (BOXES as string[]).includes(v) ? (v as ApprovalBox) : 'todo';
});
// An "all" link falls back to todo only once the probe says no; while it is pending the tab is kept.
const tab = computed<ApprovalBox>(() => (wantedTab.value === 'all' && all.denied.value ? 'todo' : wantedTab.value));
const tabs = computed<ApprovalBox[]>(() => (all.allowed.value || tab.value === 'all' ? [...BOXES, 'all'] : BOXES));

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined);
const showStatus = computed(() => tab.value !== 'todo');
const filter = computed<ApprovalInboxFilter>(() => {
  const status = str(route.query.status);
  const from = str(route.query.from);
  const to = str(route.query.to);
  return {
    module: str(route.query.module),
    status: showStatus.value && status && (INBOX_STATUSES as string[]).includes(status) ? (status as ApprovalInstanceStatus) : undefined,
    from: from && isDay(from) ? from : undefined,
    to: to && isDay(to) ? to : undefined,
  };
});
const page = ref(1);
const delegateOpen = ref(false);

const query = useApprovalInbox(tab, filter, page);
const items = computed(() => query.data.value?.items ?? []);
const pageCount = computed(() => Math.max(1, Math.ceil((query.data.value?.totalCount ?? 0) / SIZE)));
watch(pageCount, (n) => { if (page.value > n) page.value = n; });
// On the all tab a module the user cannot approve answers 403: nothing to show there, not a failure.
const loadFailed = computed(() => !!query.error.value && !query.data.value && !(tab.value === 'all' && isForbidden(query.error.value)));

const menu = useMenu();
const modules = computed(() => (menu.data.value ?? []).map((m) => ({ value: m.key, label: resolveI18n(m.name, locale.value) })));

function go(patch: Record<string, string | undefined>) {
  page.value = 1;
  const next: LocationQueryRaw = { ...route.query, ...patch };
  for (const k of Object.keys(next)) if (next[k] === undefined) delete next[k];
  void router.replace({ query: next });
}
const setTab = (v: ApprovalBox) => go({ tab: v });
const setFilter = (f: ApprovalInboxFilter) => go({ module: f.module, status: f.status, from: f.from, to: f.to });

const key = (r: ApprovalInboxItem) => `${r.taskId}:${r.instanceId}`;
const title = (r: ApprovalInboxItem) => instanceTitle(r.titleSnapshot, r.moduleName, r.recordId, locale.value);
const stage = (r: ApprovalInboxItem) => (r.status === 'pending' ? (resolveI18n(r.stageName ?? undefined, locale.value) || '—') : t(`approval.status.${r.status}`));
const moment = (v: string | null) => (v ? date.formatDate(new Date(v), 'YYYY-MM-DD HH:mm') : '—');
const open = (r: ApprovalInboxItem) => { void router.push(`/dynamic/${r.moduleKey}/${r.recordId}`); };

const columns = computed(() => [
  { name: 'title', label: t('approval.center.columns.title'), align: 'left' as const, field: 'titleSnapshot' },
  { name: 'module', label: t('approval.center.columns.module'), align: 'left' as const, field: 'moduleKey' },
  { name: 'stage', label: t('approval.center.columns.stage'), align: 'left' as const, field: 'stageName' },
  { name: 'initiator', label: t('approval.center.columns.initiator'), align: 'left' as const, field: 'initiatorName' },
  { name: 'submitted', label: t('approval.center.columns.submitted'), align: 'left' as const, field: 'submittedTime' },
  { name: 'due', label: t('approval.center.columns.due'), align: 'left' as const, field: 'dueTime' },
]);
</script>

<template>
  <q-page padding>
    <div class="row items-center no-wrap q-mb-sm q-gutter-x-sm">
      <div class="col text-h6 approval-cell">{{ t('approval.center.title') }}</div>
      <q-btn outline dense no-caps color="primary" icon="swap_horiz" :label="t('approval.delegate.button')" data-testid="delegate-open" @click="delegateOpen = true" />
    </div>
    <DelegateBanner />
    <DelegateSettingsDialog v-model="delegateOpen" />
    <q-tabs :model-value="tab" dense align="left" narrow-indicator class="text-primary" @update:model-value="(v) => setTab(v as ApprovalBox)">
      <q-tab v-for="x in tabs" :key="x" :name="x" no-caps :label="t(`approval.center.tabs.${x}`)" :data-testid="`approval-tab-${x}`" />
    </q-tabs>
    <q-separator class="q-mb-md" />
    <ApprovalInboxFilters :model-value="filter" :show-status="showStatus" :modules="modules" :mobile="$q.screen.lt.sm" @update:model-value="setFilter" />

    <div v-if="loadFailed" class="text-negative" data-testid="approval-load-failed">{{ t('approval.center.loadFailed') }}</div>
    <div v-else-if="!items.length && !query.isFetching.value" class="text-muted q-py-md" data-testid="approval-empty">{{ t(`approval.center.empty.${tab}`) }}</div>
    <q-table
      v-else flat bordered :row-key="key" :rows="items" :columns="columns" :loading="query.isFetching.value"
      :pagination="{ rowsPerPage: 0 }" :rows-per-page-options="[0]" hide-pagination :grid="$q.screen.lt.md"
    >
      <template #body="{ row, cols }">
        <q-tr
          class="row-clickable" tabindex="0" :data-testid="`approval-row-${row.instanceId}`"
          @click="open(row)" @keydown.enter.self.prevent="open(row)" @keydown.space.self.prevent="open(row)"
        >
          <q-td v-for="c in cols" :key="c.name" :align="c.align" class="approval-cell">
            <template v-if="c.name === 'title'">{{ title(row) }}</template>
            <template v-else-if="c.name === 'module'">
              {{ resolveI18n(row.moduleName, locale) }}
              <q-badge v-if="row.moduleClosed" outline color="muted" :label="t('approval.center.closed')" :data-testid="`approval-closed-${row.instanceId}`" />
            </template>
            <template v-else-if="c.name === 'stage'">{{ stage(row) }}</template>
            <template v-else-if="c.name === 'initiator'">{{ row.initiatorName || row.initiatorUserId }}</template>
            <template v-else-if="c.name === 'submitted'">{{ moment(row.submittedTime) }}</template>
            <template v-else>
              {{ moment(row.dueTime) }}
              <q-badge v-if="row.overdue" color="negative" :label="t('approval.center.overdue')" :data-testid="`approval-overdue-${row.instanceId}`" />
            </template>
          </q-td>
        </q-tr>
      </template>
      <template #item="{ row }">
        <div class="col-12 q-py-xs">
          <q-card
            flat bordered class="cursor-pointer card-clickable" tabindex="0" :aria-label="t('approval.center.openRecord', { title: title(row) })"
            :data-testid="`approval-card-${row.instanceId}`"
            @click="open(row)" @keydown.enter.self.prevent="open(row)" @keydown.space.self.prevent="open(row)"
          >
            <q-card-section class="q-pb-xs approval-cell text-weight-medium">{{ title(row) }}</q-card-section>
            <q-card-section class="q-py-xs approval-cell">
              <div class="text-caption">{{ resolveI18n(row.moduleName, locale) }} · {{ stage(row) }}</div>
              <div class="text-caption">{{ row.initiatorName || row.initiatorUserId }} · {{ moment(row.submittedTime) }}</div>
              <div v-if="row.dueTime" class="text-caption">{{ t('approval.center.columns.due') }} {{ moment(row.dueTime) }}</div>
            </q-card-section>
            <q-card-section v-if="row.overdue || row.moduleClosed" class="q-pt-xs row q-gutter-xs">
              <q-badge v-if="row.overdue" color="negative" :label="t('approval.center.overdue')" :data-testid="`approval-overdue-${row.instanceId}`" />
              <q-badge v-if="row.moduleClosed" outline color="muted" :label="t('approval.center.closed')" :data-testid="`approval-closed-${row.instanceId}`" />
            </q-card-section>
          </q-card>
        </div>
      </template>
    </q-table>
    <div v-if="pageCount > 1" class="flex flex-center q-pa-sm">
      <q-pagination v-model="page" :max="pageCount" :max-pages="5" direction-links data-testid="approval-pagination" />
    </div>
  </q-page>
</template>

<style scoped>
.approval-cell {
  overflow-wrap: anywhere;
  white-space: normal;
}
</style>
