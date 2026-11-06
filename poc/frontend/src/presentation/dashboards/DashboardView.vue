<script setup lang="ts">
import { Notify, useQuasar } from 'quasar';
import { computed, ref, toRef, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useDashboard, useRestoreDashboard, useSaveDashboardLayout,
} from 'src/application/dynamic/queries/dashboards-queries';
import {
  addCard, applyPositions, FILTER_PRESETS, removeCard, sameLayout, updateCard,
} from 'src/domain/dynamic/dashboard-layout';
import type { DashboardLayout } from 'src/domain/dynamic/dashboard-types';
import type { ReportColumn, ReportViz } from 'src/domain/dynamic/report-types';
import AddCardDialog from './AddCardDialog.vue';
import CardOptionsDialog from './CardOptionsDialog.vue';
import DashboardCardView from './DashboardCardView.vue';
import DashboardGrid from './DashboardGrid.vue';

/**
 * 一張儀表板：標題列、暫時的日期篩選、個人版提示、卡片版面。拖放與卡片異動都立刻存檔
 * （指派版由後端另存個人版，自己的儀表板直接存）；日期篩選只在畫面上，不存、也不算調整。
 * allowEdit 為 false 時（首頁）只能看，不能改版面。
 */
const props = defineProps<{ dashboardId: number; allowEdit: boolean; linkable: { id: number; name: string }[] }>();
const { t } = useI18n();
const $q = useQuasar();
const narrow = computed(() => $q.screen.lt.sm);

const dash = useDashboard(toRef(props, 'dashboardId'));
const save = useSaveDashboardLayout();
const restore = useRestoreDashboard();
const detail = computed(() => dash.data.value);

const layout = ref<DashboardLayout>({ cards: [] });
let inFlight = 0;
// 儲存進行中不要被較舊的回應蓋掉本機的版面
watch(detail, (d) => { if (d && inFlight === 0) layout.value = d.layout; }, { immediate: true });
watch(() => props.dashboardId, () => { editing.value = false; filterKey.value = null; });

const editing = ref(false);
const saveError = ref(false);
let chain: Promise<unknown> = Promise.resolve();
function persist(next: DashboardLayout, name?: string) {
  layout.value = next;
  saveError.value = false;
  inFlight++;
  chain = chain
    .then(() => save.mutateAsync({ id: props.dashboardId, layout: next, name }))
    .catch(() => { saveError.value = true; })
    .finally(() => {
      inFlight--;
      if (inFlight === 0 && detail.value) layout.value = saveError.value ? detail.value.layout : layout.value;
    });
}

const filterKey = ref<string | null>(null);
const filterRange = computed(() => {
  const p = FILTER_PRESETS.find((x) => x.key === filterKey.value);
  return p ? { kind: p.kind, n: p.n ?? null } : null;
});
const filterOptions = computed(() => FILTER_PRESETS.map((p) => ({ value: p.key, label: t(`dashboards.filter.${p.key}`) })));

const isOwn = computed(() => detail.value?.kind === 'mine');
const nameDraft = ref('');
watch(detail, (d) => { if (d) nameDraft.value = d.name; }, { immediate: true });
function rename() {
  const name = nameDraft.value.trim();
  if (!name) { nameDraft.value = detail.value?.name ?? ''; return; }
  if (name !== detail.value?.name) persist(layout.value, name);
}

function onMove(positions: { i: string; x: number; y: number; w: number; h: number }[]) {
  const next = applyPositions(layout.value, positions);
  if (!sameLayout(next, layout.value)) persist(next);
}

const addOpen = ref(false);
function onAdd(reportId: number, viz: ReportViz) {
  addOpen.value = false;
  persist(addCard(layout.value, reportId, viz));
}

const optionsCardId = ref<string | null>(null);
const optionsColumns = ref<ReportColumn[]>([]);
const optionsCard = computed(() => layout.value.cards.find((c) => c.id === optionsCardId.value) ?? null);
function openOptions(cardId: string, columns: ReportColumn[]) { optionsColumns.value = columns; optionsCardId.value = cardId; }
function onOptionsSave(change: { title: string | null; viz: ReportViz; options: import('src/domain/dynamic/dashboard-types').CardOptions }) {
  const id = optionsCardId.value!;
  optionsCardId.value = null;
  persist(updateCard(layout.value, id, change));
}

const confirmRestore = ref(false);
function doRestore() {
  restore.mutate(props.dashboardId, {
    onSuccess: () => { confirmRestore.value = false; Notify.create({ type: 'positive', message: t('dashboards.restored') }); },
    onError: () => { confirmRestore.value = false; Notify.create({ type: 'negative', message: t('dashboards.restoreFailed') }); },
  });
}
const linkVisible = (id: number | null | undefined) => id != null && props.linkable.some((d) => d.id === id);
</script>

<template>
  <div data-testid="dashboard-view">
    <div v-if="dash.isError.value" class="text-negative" data-testid="dashboard-error">{{ t('dashboards.notFound') }}</div>
    <div v-else-if="dash.isPending.value" class="q-pa-md"><q-spinner /></div>
    <template v-else-if="detail">
      <div class="row items-center q-gutter-sm q-mb-sm">
        <q-input
          v-if="editing && isOwn" v-model="nameDraft" dense outlined maxlength="200" style="min-width: 220px"
          :aria-label="t('dashboards.name')" data-testid="dashboard-name-input" @blur="rename" @keyup.enter="rename"
        />
        <div v-else class="text-h6" data-testid="dashboard-name">{{ detail.name }}</div>
        <q-badge v-if="detail.adjusted" color="primary" data-testid="dashboard-adjusted">{{ t('dashboards.adjusted') }}</q-badge>
        <q-space />
        <q-select
          v-model="filterKey" :options="filterOptions" emit-value map-options clearable dense outlined style="min-width: 180px"
          :label="t('dashboards.filter.label')" :hint="filterKey ? t('dashboards.filter.hint') : undefined" data-testid="dashboard-filter"
        />
        <template v-if="allowEdit">
          <q-btn v-if="editing" outline no-caps icon="add" :label="t('dashboards.card.add')" data-testid="dashboard-add-card" @click="addOpen = true" />
          <q-btn
            :outline="!editing" :unelevated="editing" :color="editing ? 'primary' : undefined" no-caps :icon="editing ? 'check' : 'edit'"
            :label="editing ? t('dashboards.doneEditing') : t('dashboards.edit')" data-testid="dashboard-edit-toggle" @click="editing = !editing"
          />
        </template>
        <span v-if="save.isPending.value" class="text-caption text-muted" data-testid="dashboard-saving">{{ t('dashboards.saving') }}</span>
        <span v-if="saveError" class="text-caption text-negative" data-testid="dashboard-save-error">{{ t('dashboards.saveFailed') }}</span>
      </div>

      <q-banner v-if="detail.hasUpdate" rounded dense class="q-mb-sm bg-info-soft" data-testid="dashboard-update-banner">
        <span class="text-weight-bold">{{ t('dashboards.updated') }}</span>
        <span class="q-ml-sm">{{ t('dashboards.updatedHint') }}</span>
        <template #action>
          <q-btn flat no-caps :label="t('dashboards.restore')" data-testid="dashboard-restore" @click="confirmRestore = true" />
        </template>
      </q-banner>

      <div v-if="!layout.cards.length" class="text-muted q-pa-md" data-testid="dashboard-empty">
        {{ allowEdit ? t('dashboards.emptyEditable') : t('dashboards.empty') }}
      </div>
      <DashboardGrid v-else :cards="layout.cards" :editable="editing && !narrow" :narrow="narrow" @change="onMove">
        <template #card="{ card }">
          <DashboardCardView
            :dashboard-id="dashboardId" :card="card" :report-name="detail.reportNames[String(card.reportId)]" :filter-range="filterRange"
            :editing="editing" :link-visible="linkVisible(card.options.linkDashboardId)"
            @settings="openOptions" @remove="(id: string) => persist(removeCard(layout, id))"
          />
        </template>
      </DashboardGrid>
    </template>

    <AddCardDialog :open="addOpen" @close="addOpen = false" @add="onAdd" />
    <CardOptionsDialog
      :card="optionsCard" :columns="optionsColumns" :report-name="optionsCard ? detail?.reportNames[String(optionsCard.reportId)] : undefined"
      :dashboards="linkable.filter((d) => d.id !== dashboardId)" @close="optionsCardId = null" @save="onOptionsSave"
    />
    <q-dialog v-model="confirmRestore">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('dashboards.restore') }}</q-card-section>
        <q-card-section>{{ t('dashboards.confirmRestore') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" data-testid="restore-cancel" @click="confirmRestore = false" />
          <q-btn color="primary" :label="t('dashboards.restore')" :loading="restore.isPending.value" data-testid="restore-confirm" @click="doRestore" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
