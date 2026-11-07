<script setup lang="ts">
import { Notify, useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAdminGroups } from 'src/application/dynamic/queries/admin-queries';
import {
  useAdminCreateDashboard, useAdminDashboard, useAdminDashboards, useAdminDeleteDashboard, useAdminReorderDashboards,
  useAdminSetAssignments, useAdminUpdateDashboard,
} from 'src/application/dynamic/queries/dashboards-queries';
import { useReports } from 'src/application/dynamic/queries/reports-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { addCard, applyPositions, removeCard, sameLayout } from 'src/domain/dynamic/dashboard-layout';
import type { DashboardLayout } from 'src/domain/dynamic/dashboard-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ReportViz } from 'src/domain/dynamic/report-types';
import AddCardDialog from 'src/presentation/dashboards/AddCardDialog.vue';
import AdminCardPreview from 'src/presentation/dashboards/AdminCardPreview.vue';
import DashboardGrid from 'src/presentation/dashboards/DashboardGrid.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';

/**
 * 指派版管理：新增、改名、刪除、上下移排序、勾選群組、編輯版面。
 * 卡片內顯示預覽：後端以管理員自己的權限計算真實數字（不繞過權限，看不到的卡片顯示「無法預覽」），畫面上明講這一點。
 */
const { t, locale } = useI18n();
const $q = useQuasar();
const narrow = computed(() => $q.screen.lt.sm);
const list = useAdminDashboards();
const groups = useAdminGroups();
const reports = useReports();
const forbidden = computed(() => isForbidden(list.error.value));
const items = computed(() => list.data.value ?? []);

const selectedId = ref<number | null>(null);
const detail = useAdminDashboard(selectedId);
const name = ref('');
const layout = ref<DashboardLayout>({ cards: [] });
const groupIds = ref<number[]>([]);
const version = ref(0);
const error = ref('');
watch(detail.data, (d) => {
  if (!d) return;
  name.value = d.name;
  layout.value = d.layout;
  groupIds.value = [...d.groupIds];
  version.value = d.version;
  error.value = '';
}, { immediate: true });

const reportName = (id: number) => reports.data.value?.find((r) => r.id === id)?.name ?? `#${id}`;
const groupName = (g: { nameI18n: Parameters<typeof resolveI18n>[0] }) => resolveI18n(g.nameI18n, locale.value);
const failText = (e: unknown, fallback: string) => ((e as ApiError).status === 409 ? t('dashboards.admin.conflict') : fallback);
const done = (message: string) => Notify.create({ type: 'positive', message });

const create = useAdminCreateDashboard();
const createOpen = ref(false);
const newName = ref('');
function doCreate() {
  const n = newName.value.trim();
  if (!n) return;
  create.mutate({ name: n, layout: { cards: [] } }, {
    onSuccess: (d) => { createOpen.value = false; newName.value = ''; selectedId.value = d.id; },
    onError: () => { error.value = t('dashboards.saveFailed'); },
  });
}

const update = useAdminUpdateDashboard();
function saveLayout() {
  error.value = '';
  update.mutate({ id: selectedId.value!, name: name.value.trim(), layout: layout.value, version: version.value }, {
    onSuccess: () => done(t('dashboards.admin.saved')),
    onError: (e) => { error.value = failText(e, t('dashboards.saveFailed')); },
  });
}

const assign = useAdminSetAssignments();
function saveAssignments() {
  error.value = '';
  assign.mutate({ id: selectedId.value!, groupIds: groupIds.value }, {
    onSuccess: () => done(t('dashboards.admin.saved')),
    onError: (e) => { error.value = failText(e, t('dashboards.saveFailed')); },
  });
}

const reorder = useAdminReorderDashboards();
function move(index: number, by: -1 | 1) {
  const ids = items.value.map((d) => d.id);
  const j = index + by;
  if (j < 0 || j >= ids.length) return;
  [ids[index], ids[j]] = [ids[j], ids[index]];
  reorder.mutate(ids, { onError: () => Notify.create({ type: 'negative', message: t('dashboards.saveFailed') }) });
}

const addOpen = ref(false);
function onAdd(reportId: number, viz: ReportViz) {
  addOpen.value = false;
  layout.value = addCard(layout.value, reportId, viz);
}
function onMove(positions: { i: string; x: number; y: number; w: number; h: number }[]) {
  const next = applyPositions(layout.value, positions);
  if (!sameLayout(next, layout.value)) layout.value = next;
}

const removeOpen = ref(false);
const remove = useAdminDeleteDashboard();
function doRemove() {
  remove.mutate(selectedId.value!, {
    onSuccess: () => { removeOpen.value = false; selectedId.value = null; },
    onError: () => { removeOpen.value = false; error.value = t('dashboards.saveFailed'); },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('dashboards.admin.title') }}</div>
        <q-space />
        <q-btn color="primary" icon="add" no-caps :label="t('dashboards.create')" data-testid="admin-dashboard-create" @click="createOpen = true" />
      </div>
      <div class="text-caption text-muted q-mb-sm">{{ t('dashboards.admin.hint') }}</div>

      <q-list bordered separator class="q-mb-md" data-testid="admin-dashboard-list">
        <q-item v-if="!items.length" data-testid="admin-dashboard-empty"><q-item-section class="text-muted">{{ t('dashboards.admin.empty') }}</q-item-section></q-item>
        <q-item
          v-for="(d, i) in items" :key="d.id" clickable :active="d.id === selectedId" :data-testid="`admin-dashboard-${d.id}`" @click="selectedId = d.id"
        >
          <q-item-section>
            <q-item-label>{{ d.name }}</q-item-label>
            <q-item-label caption>{{ t('dashboards.admin.summary', { cards: d.cardCount, groups: d.groupIds.length, version: d.version }) }}</q-item-label>
          </q-item-section>
          <q-item-section side>
            <div class="row no-wrap">
              <q-btn flat dense round icon="arrow_upward" :disable="i === 0" :aria-label="t('dashboards.admin.moveUp')" :data-testid="`admin-up-${d.id}`" @click.stop="move(i, -1)" />
              <q-btn flat dense round icon="arrow_downward" :disable="i === items.length - 1" :aria-label="t('dashboards.admin.moveDown')" :data-testid="`admin-down-${d.id}`" @click.stop="move(i, 1)" />
            </div>
          </q-item-section>
        </q-item>
      </q-list>

      <div v-if="selectedId !== null" data-testid="admin-dashboard-editor">
        <div v-if="detail.isError.value" class="text-negative">{{ t('dashboards.notFound') }}</div>
        <template v-else-if="detail.data.value">
          <div class="row items-center q-gutter-sm q-mb-md">
            <q-input v-model="name" dense outlined maxlength="200" style="min-width: 240px" :label="t('dashboards.name')" data-testid="admin-name" />
            <q-btn outline no-caps icon="add" :label="t('dashboards.card.add')" data-testid="admin-add-card" @click="addOpen = true" />
            <q-btn color="primary" no-caps :label="t('dashboards.admin.saveLayout')" :loading="update.isPending.value" :disable="!name.trim()" data-testid="admin-save-layout" @click="saveLayout" />
            <q-space />
            <q-btn flat color="negative" no-caps icon="delete" :label="t('dashboards.delete')" data-testid="admin-delete" @click="removeOpen = true" />
          </div>
          <div v-if="error" class="text-negative q-mb-sm" data-testid="admin-error">{{ error }}</div>

          <div class="text-subtitle2 q-mb-xs">{{ t('dashboards.admin.assignTo') }}</div>
          <div class="q-mb-md">
            <q-option-group v-model="groupIds" type="checkbox" inline :options="(groups.data.value ?? []).map((g) => ({ value: g.id, label: groupName(g) }))" data-testid="admin-groups" />
            <q-btn class="q-mt-sm" outline no-caps :label="t('dashboards.admin.saveAssign')" :loading="assign.isPending.value" data-testid="admin-save-assign" @click="saveAssignments" />
          </div>
          <div v-if="!groupIds.length" class="text-caption text-muted q-mb-md" data-testid="admin-unassigned">{{ t('dashboards.admin.unassigned') }}</div>

          <div v-if="layout.cards.length" class="text-caption text-muted q-mb-sm" data-testid="admin-preview-notice">{{ t('dashboards.admin.previewNotice') }}</div>
          <div v-if="!layout.cards.length" class="text-muted" data-testid="admin-layout-empty">{{ t('dashboards.emptyEditable') }}</div>
          <DashboardGrid v-else :cards="layout.cards" :editable="!narrow" :narrow="narrow" @change="onMove">
            <template #card="{ card }">
              <q-card flat bordered class="full-height column no-wrap" :data-testid="`admin-card-${card.id}`">
                <q-card-section class="row items-center no-wrap q-py-sm">
                  <q-icon name="drag_indicator" class="card-drag-handle cursor-move q-mr-xs" />
                  <div class="col ellipsis">{{ card.title || reportName(card.reportId) }}</div>
                  <q-badge outline color="primary" :label="t(`reports.designer.vizName.${card.viz}`)" class="q-mx-sm" />
                  <q-btn flat dense round size="sm" icon="close" :aria-label="t('dashboards.card.remove')" :data-testid="`admin-remove-${card.id}`" @click="layout = removeCard(layout, card.id)" />
                </q-card-section>
                <AdminCardPreview class="col" :card="card" />
              </q-card>
            </template>
          </DashboardGrid>
        </template>
      </div>
    </template>

    <AddCardDialog :open="addOpen" @close="addOpen = false" @add="onAdd" />
    <q-dialog v-model="createOpen">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('dashboards.create') }}</q-card-section>
        <q-card-section><q-input v-model="newName" dense outlined autofocus maxlength="200" :label="t('dashboards.name')" data-testid="admin-new-name" @keyup.enter="doCreate" /></q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" @click="createOpen = false" />
          <q-btn color="primary" :label="t('reports.save')" :disable="!newName.trim()" :loading="create.isPending.value" data-testid="admin-new-confirm" @click="doCreate" />
        </q-card-actions>
      </q-card>
    </q-dialog>
    <q-dialog v-model="removeOpen">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('dashboards.delete') }}</q-card-section>
        <q-card-section>{{ t('dashboards.admin.confirmDelete') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" @click="removeOpen = false" />
          <q-btn color="negative" :label="t('dashboards.delete')" :loading="remove.isPending.value" data-testid="admin-delete-confirm" @click="doRemove" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-page>
</template>
