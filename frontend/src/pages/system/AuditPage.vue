<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useAudit, useAuditActions } from 'src/application/dynamic/queries/ops-queries';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import DateSelect from 'src/components/framework/DateSelect.vue';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { formatTime, labelOr, localDayEndUtc, localDayStartUtc } from 'src/domain/ops/ops-format';
import type { AuditFilter, AuditSummary } from 'src/domain/ops/ops-types';
import AuditDetailPanel from 'src/presentation/ops/AuditDetailPanel.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 稽核頁（維運設計 §17.3）：篩選列＋清單（Id 遞減 keyset，捲到底自動載入下一頁）；點開是詳細。
// 桌機詳細與清單並排，其他寬度用對話框（手機全螢幕）。`?id=` 直接開詳細（通知與事件樣本的連結），`?targetKind=&targetId=` 帶入目標篩選。
const { t, te } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();

const users = useUsers();
const actions = useAuditActions();
const GROUPS = computed(() => [...new Set((actions.data.value ?? []).map((a) => a.group))]);
const CHANNELS = ['ui', 'job', 'mcp', 'opsCenter'];
const RESULTS = ['success', 'denied', 'invalid', 'failed'];

const form = ref({ from: null as string | null, to: null as string | null, actor: null as string | null, action: null as string | null,
  channel: null as string | null, result: null as string | null });
/** 篩選列：手機預設收起，其他寬度展開。 */
const showFilters = ref(!$q.screen.lt.sm);
/** 動作選單的值：`group:<分組>` 或動作代碼。 */
const actionOptions = computed(() => [
  ...GROUPS.value.map((g) => ({ value: `group:${g}`, label: t('audit.groupOption', { group: labelOr(t, te, `audit.groups.${g}`, g) }) })),
  ...(actions.data.value ?? []).map((a) => ({ value: a.code, label: labelOr(t, te, a.labelKey, a.code) })),
]);
const actorOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: `${u.displayName || u.userId}（${u.userId}）` })));
const channelOptions = computed(() => CHANNELS.map((c) => ({ value: c, label: t(`audit.channel.${c}`) })));
const resultOptions = computed(() => RESULTS.map((r) => ({ value: r, label: t(`audit.result.${r}`) })));

/** 篩選組成的 query（空值不送；目標篩選來自網址）。 */
const filter = computed<AuditFilter>(() => {
  const f = form.value;
  const a = f.action;
  return {
    ...(f.from ? { fromUtc: localDayStartUtc(f.from) } : {}),
    ...(f.to ? { toUtc: localDayEndUtc(f.to) } : {}),
    ...(f.actor ? { actorUserId: f.actor } : {}),
    ...(a?.startsWith('group:') ? { actionGroup: a.slice(6) } : a ? { action: a } : {}),
    ...(f.channel ? { channel: f.channel } : {}),
    ...(f.result ? { result: f.result } : {}),
    ...(route.query.targetKind ? { targetKind: String(route.query.targetKind) } : {}),
    ...(route.query.targetId ? { targetId: String(route.query.targetId) } : {}),
  };
});
const query = useAudit(filter);
const rows = computed(() => query.data.value?.pages.flatMap((p) => p.items) ?? []);
const err = computed(() => query.error.value as ApiError | null);
const forbidden = computed(() => isForbidden(err.value));
const filterErrors = computed(() => Object.values(err.value?.errors ?? {}).flat());
const targetFiltered = computed(() => !!route.query.targetKind || !!route.query.targetId);
function clearTarget() { void router.replace({ query: { ...route.query, targetKind: undefined, targetId: undefined } }); }

const selected = computed<number | null>(() => {
  const n = Number(route.query.id);
  return Number.isInteger(n) && n > 0 ? n : null;
});
function open(id: number | null) { void router.replace({ query: { ...route.query, id: id ?? undefined } }); }
const sideBySide = computed(() => $q.screen.xl);

const actorText = (a: AuditSummary) => a.actorName ?? a.actorUserId ?? t(`audit.actorKind.${a.actorKind}`);
const targetText = (a: AuditSummary) => [a.targetKind && labelOr(t, te, `audit.targetKind.${a.targetKind}`, a.targetKind), a.targetLabel ?? a.targetId]
  .filter(Boolean).join('：');
const columns = computed<QTableColumn<AuditSummary>[]>(() => [
  { name: 'time', label: t('audit.cols.time'), align: 'left', field: (a) => formatTime(a.occurredUtc) },
  { name: 'actor', label: t('audit.cols.actor'), align: 'left', field: actorText },
  { name: 'action', label: t('audit.cols.action'), align: 'left', field: (a) => labelOr(t, te, `audit.actions.${a.action}`, a.action) },
  { name: 'target', label: t('audit.cols.target'), align: 'left', field: targetText },
  { name: 'result', label: t('audit.cols.result'), align: 'left', field: (a) => labelOr(t, te, `audit.result.${a.result}`, a.result) },
  { name: 'channel', label: t('audit.cols.channel'), align: 'left', field: (a) => labelOr(t, te, `audit.channel.${a.channel}`, a.channel) },
]);
/** 平板與並排時隱藏來源管道（§17.3 版型表）。 */
const visibleColumns = computed(() => columns.value.map((c) => c.name)
  .filter((n) => n !== 'channel' || !($q.screen.sm || (sideBySide.value && selected.value !== null))));

function onLoad(_index: number, done: (stop?: boolean) => void) {
  if (!query.hasNextPage.value) { done(true); return; }
  void query.fetchNextPage().then(() => done(!query.hasNextPage.value));
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="text-h6 q-mb-sm">{{ t('audit.title') }}</div>
      <q-expansion-item v-model="showFilters" dense icon="filter_list" :label="t('ops.filters')" class="q-mb-sm" data-testid="audit-filters">
        <div class="row q-col-gutter-sm q-pa-sm">
          <div class="col-12 col-sm-6 col-md-4 col-xl-2"><DateSelect v-model="form.from" :label="t('audit.from')" data-testid="audit-from" /></div>
          <div class="col-12 col-sm-6 col-md-4 col-xl-2"><DateSelect v-model="form.to" :label="t('audit.to')" data-testid="audit-to" /></div>
          <div class="col-12 col-sm-6 col-md-4 col-xl-2">
            <q-select v-model="form.actor" :options="actorOptions" clearable emit-value map-options dense outlined :label="t('audit.cols.actor')" data-testid="audit-actor" />
          </div>
          <div class="col-12 col-sm-6 col-md-4 col-xl-2">
            <q-select v-model="form.action" :options="actionOptions" clearable emit-value map-options dense outlined :label="t('audit.cols.action')" data-testid="audit-action" />
          </div>
          <div class="col-12 col-sm-6 col-md-4 col-xl-2">
            <q-select v-model="form.channel" :options="channelOptions" clearable emit-value map-options dense outlined :label="t('audit.cols.channel')" data-testid="audit-channel" />
          </div>
          <div class="col-12 col-sm-6 col-md-4 col-xl-2">
            <q-select v-model="form.result" :options="resultOptions" clearable emit-value map-options dense outlined :label="t('audit.cols.result')" data-testid="audit-result" />
          </div>
        </div>
        <div class="text-caption text-muted q-px-sm">{{ t('audit.defaultRange') }}</div>
      </q-expansion-item>
      <q-banner v-if="targetFiltered" dense class="bg-info-soft q-mb-sm" data-testid="audit-target-filter">
        {{ t('audit.targetFilter', { target: `${route.query.targetKind ?? ''} ${route.query.targetId ?? ''}` }) }}
        <template #action><q-btn flat dense no-caps color="primary" :label="t('audit.clearTarget')" @click="clearTarget" /></template>
      </q-banner>
      <div v-for="m in filterErrors" :key="m" class="text-negative q-mb-sm" data-testid="audit-filter-error">{{ m }}</div>

      <div class="row q-col-gutter-md">
        <div :class="sideBySide && selected !== null ? 'col-8' : 'col-12'">
          <q-infinite-scroll :offset="200" :disable="!query.hasNextPage.value" @load="onLoad">
            <q-table
              flat bordered row-key="id" :rows="rows" :columns="columns" :visible-columns="visibleColumns" :loading="query.isFetching.value"
              :rows-per-page-options="[0]" hide-pagination :no-data-label="t('audit.empty')" :grid="$q.screen.lt.sm" data-testid="audit-table"
            >
              <template #body="{ row, cols }">
                <q-tr
                  class="row-clickable" tabindex="0" :class="{ 'audit-selected': row.id === selected }" :data-testid="`audit-row-${row.id}`"
                  @click="open(row.id)" @keydown.enter.self.prevent="open(row.id)"
                >
                  <q-td v-for="c in cols" :key="c.name" style="white-space: normal; overflow-wrap: anywhere">{{ c.value }}</q-td>
                </q-tr>
              </template>
              <template #item="{ row }">
                <div class="col-12 q-py-xs">
                  <q-card
                    flat bordered class="cursor-pointer card-clickable" tabindex="0" :data-testid="`audit-card-${row.id}`"
                    @click="open(row.id)" @keydown.enter.self.prevent="open(row.id)"
                  >
                    <q-card-section>
                      <div style="overflow-wrap: anywhere">{{ labelOr(t, te, `audit.actions.${row.action}`, row.action) }} · {{ labelOr(t, te, `audit.result.${row.result}`, row.result) }}</div>
                      <div class="text-caption text-muted" style="overflow-wrap: anywhere">{{ formatTime(row.occurredUtc) }} · {{ actorText(row) }}</div>
                      <div v-if="targetText(row)" class="text-caption" style="overflow-wrap: anywhere">{{ targetText(row) }}</div>
                    </q-card-section>
                  </q-card>
                </div>
              </template>
            </q-table>
          </q-infinite-scroll>
        </div>
        <div v-if="sideBySide && selected !== null" class="col-4">
          <AuditDetailPanel :audit-id="selected" class="audit-side" @close="open(null)" />
        </div>
      </div>

      <q-dialog
        :model-value="!sideBySide && selected !== null" :maximized="$q.screen.lt.sm" :position="$q.screen.lt.sm ? 'standard' : 'right'" full-height
        @update:model-value="(v) => { if (!v) open(null); }"
      >
        <AuditDetailPanel v-if="selected !== null" :audit-id="selected" class="audit-dialog" @close="open(null)" />
      </q-dialog>
    </template>
  </q-page>
</template>

<style scoped>
.audit-dialog { width: 560px; max-width: 100vw; }
.audit-selected { background: var(--t-selected-bg); }
.audit-side { position: sticky; top: 8px; max-height: calc(100vh - 120px); }
</style>
