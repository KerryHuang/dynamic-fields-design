<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, nextTick, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useApprovalFlows, useDeleteApprovalFlow, useReorderApprovalFlows, useSaveApprovalFlow } from 'src/application/dynamic/queries/approval-flow-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import RowActionMenu from 'src/components/framework/RowActionMenu.vue';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import { joinList } from 'src/i18n/translate';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import { conditionSummary } from 'src/presentation/dynamic/rules/condition-summary';
import type { ApiError } from 'src/services/api/http';
import FlowEditorDialog from './FlowEditorDialog.vue';
import { groupFlows, moveKeys, moveKeyTo } from './flow-groups';

const props = defineProps<{ module: ModuleDetail; focusTarget?: number | null }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const mobile = computed(() => $q.screen.lt.sm);

const key = computed(() => props.module.key);
const unsupported = computed(() => !!props.module.detailOf || props.module.kind === 'extension');
const workflow = useWorkflowQuery(key);
const flows = useApprovalFlows(key, () => !unsupported.value);
const me = useMe();
const saveFlow = useSaveApprovalFlow(key);
const reorder = useReorderApprovalFlows(key);
const remove = useDeleteApprovalFlow(key);

const readonly = computed(() => !me.data.value?.designer.edit);
const busy = computed(() => saveFlow.isPending.value || reorder.isPending.value || remove.isPending.value);
const fields = computed(() => toSchemaFields(props.module));

const statusField = computed(() => props.module.blocks.flatMap((b) => b.fields).find((f) => f.id === workflow.data.value?.statusFieldId));
const optionLabel = (id: number) => {
  const o = statusField.value?.options.find((x) => x.id === id);
  return o ? resolveI18n(o.label, locale.value) : String(id);
};
const groups = computed(() => groupFlows(flows.data.value ?? [], workflow.data.value ?? undefined, optionLabel, props.focusTarget));
const summary = (f: ApprovalFlow) => conditionSummary(f.definition.when, fields.value, t, locale.value);

function fail(e: unknown) {
  const err = e as ApiError;
  const lines = leftoverMessages(err, []);
  $q.notify({ type: 'negative', message: lines.length ? joinList(lines) : t('designer.saveFailed') });
}

// ---- toggle active: the whole flow goes back unchanged except `active` (the backend keeps the version when the definition is the same) ----
const confirmingOff = ref<ApprovalFlow | null>(null);
function setActive(f: ApprovalFlow, active: boolean) {
  const { key: k, name, toOptionId, priority, definition } = f;
  saveFlow.mutate({ key: k, input: { key: k, name, toOptionId, priority, active, definition } }, {
    onSuccess: () => $q.notify({ type: 'positive', message: t(active ? 'approval.designer.list.activated' : 'approval.designer.list.deactivated') }),
    onError: fail,
  });
}
function onToggle(f: ApprovalFlow, on: boolean) {
  if (on) setActive(f, true);
  else confirmingOff.value = f;
}
function confirmOff() {
  const f = confirmingOff.value;
  confirmingOff.value = null;
  if (f) setActive(f, false);
}

// ---- order ----
function applyOrder(toOptionId: number, keys: string[] | null) {
  // The list is the cached order until the reorder and its refetch finish; a second move now would be computed from stale keys.
  if (!keys || readonly.value || busy.value) return;
  reorder.mutate({ toOptionId, keys }, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('approval.designer.list.reordered') }),
    onError: fail,
  });
}
const move = (toOptionId: number, list: ApprovalFlow[], f: ApprovalFlow, dir: -1 | 1) =>
  applyOrder(toOptionId, moveKeys(list.map((x) => x.key), f.key, dir));
const dragging = ref<{ key: string; toOptionId: number } | null>(null);
function onDrop(toOptionId: number, list: ApprovalFlow[], target: ApprovalFlow) {
  const d = dragging.value;
  dragging.value = null;
  if (d && d.toOptionId === toOptionId) applyOrder(toOptionId, moveKeyTo(list.map((x) => x.key), d.key, target.key));
}

// ---- delete (unused flows only) ----
const confirmingDelete = ref<ApprovalFlow | null>(null);
function confirmDelete() {
  const f = confirmingDelete.value;
  confirmingDelete.value = null;
  if (f) remove.mutate(f.key, { onSuccess: () => $q.notify({ type: 'positive', message: t('approval.designer.list.deleted') }), onError: fail });
}

// ---- create / edit dialog ----
const dialogOpen = ref(false);
// The dialog unmounts as soon as it closes, so the element that opened it gets its focus back by hand.
let opener: HTMLElement | null = null;
// The opener can be gone by then (a menu item that closed, a card that moved to another group or was rebuilt on save): the flow's card, or the add button, takes the focus.
// After a save the list is refetched first, so the card to focus is the one of the new list (a created flow's card, else the add button).
let savedKey: string | null | undefined;
function onSaved(f: ApprovalFlow | undefined) { savedKey = f?.key ?? editing.value?.key ?? null; }
const focusTestid = (testid: string) => document.querySelector<HTMLElement>(`[data-testid="${testid}"]`);
watch(dialogOpen, async (open) => {
  if (open) return;
  const afterSave = savedKey !== undefined;
  const key = savedKey;
  savedKey = undefined;
  if (afterSave) await flows.refetch({ cancelRefetch: false });
  await nextTick();
  const target = afterSave
    ? (key ? focusTestid(`flow-card-${key}`) : null) ?? focusTestid('flow-add')
    : opener?.isConnected ? opener : focusTestid(editing.value ? `flow-card-${editing.value.key}` : 'flow-add');
  target?.focus?.();
  opener = null;
});
const editing = ref<ApprovalFlow | undefined>();
const preset = ref<number | null>(null);
function openCreate(toOptionId: number | null) {
  if (readonly.value) return;
  opener = document.activeElement as HTMLElement | null;
  editing.value = undefined;
  preset.value = toOptionId;
  dialogOpen.value = true;
}
function openEdit(f: ApprovalFlow) {
  if (readonly.value) return;
  opener = document.activeElement as HTMLElement | null;
  editing.value = f;
  preset.value = null;
  dialogOpen.value = true;
}
// The state machine tab's "create the flow" link: a target that has no flow yet goes straight to the create dialog (one that already has flows is only highlighted).
const wantCreate = ref<number | null>(null);
watch(() => props.focusTarget, (id) => { wantCreate.value = id ?? null; }, { immediate: true });
watch([wantCreate, () => flows.data.value, () => me.data.value], ([id, list]) => {
  if (id == null || !list || !me.data.value) return;
  wantCreate.value = null;
  if (!list.some((f) => f.toOptionId === id)) openCreate(id);
}, { immediate: true });

// ---- focus a group when the state machine tab sent us here ----
const highlighted = ref<number | null>(null);
let timer: ReturnType<typeof setTimeout> | undefined;
watch(() => props.focusTarget, async (id) => {
  if (id == null) return;
  highlighted.value = id;
  await nextTick();
  document.querySelector(`[data-testid="flow-group-${id}"]`)?.scrollIntoView?.({ block: 'start', behavior: 'smooth' });
  clearTimeout(timer);
  timer = setTimeout(() => { highlighted.value = null; }, 2500);
}, { immediate: true });

const nameOf = (f: ApprovalFlow) => resolveI18n(f.name, locale.value);
const actionsOf = (list: ApprovalFlow[], f: ApprovalFlow, i: number) => [
  { label: t('approval.designer.list.edit'), run: () => openEdit(f), testid: `flow-edit-${f.key}` },
  { label: t('approval.designer.list.moveUp'), run: () => move(f.toOptionId, list, f, -1), disable: busy.value || i === 0, testid: `flow-up-${f.key}` },
  { label: t('approval.designer.list.moveDown'), run: () => move(f.toOptionId, list, f, 1), disable: busy.value || i === list.length - 1, testid: `flow-down-${f.key}` },
  f.used
    ? { label: t('approval.designer.list.usedMenu'), run: () => {}, disable: true, testid: `flow-used-${f.key}` }
    : { label: t('approval.designer.list.delete'), run: () => { confirmingDelete.value = f; }, disable: busy.value, negative: true, testid: `flow-delete-${f.key}` },
];
</script>

<template>
  <div v-if="unsupported" class="text-muted">{{ t('docflow.workflow.unsupported') }}</div>
  <div v-else-if="workflow.isError.value || flows.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="workflow.isPending.value || flows.isPending.value" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else-if="workflow.data.value === null" class="text-muted" data-testid="flows-no-workflow">{{ t('approval.designer.list.noWorkflow') }}</div>
  <div v-else class="column q-gutter-y-md" data-testid="flows-tab">
    <div class="row items-center no-wrap">
      <div class="col text-caption text-muted">{{ t('approval.designer.list.hint') }}</div>
      <q-btn v-if="!readonly" class="q-ml-sm" color="primary" dense icon="add" :label="t('approval.designer.list.add')" data-testid="flow-add" @click="openCreate(null)" />
    </div>

    <section
      v-for="g in groups" :key="g.toOptionId" :class="{ 'flow-group--focus': highlighted === g.toOptionId }" class="flow-group"
      role="group" :aria-label="t('approval.designer.list.groupAria', { state: g.label })" :data-testid="`flow-group-${g.toOptionId}`"
    >
      <div class="text-subtitle1 text-weight-medium">{{ g.label }}</div>
      <div v-if="g.entries.length" class="text-caption text-muted" :data-testid="`flow-entries-${g.toOptionId}`">
        {{ t('approval.designer.list.entries', { entries: joinList(g.entries, locale) }) }}
      </div>
      <div v-else-if="g.flows.length" class="text-caption text-warning" :data-testid="`flow-no-entry-${g.toOptionId}`">{{ t('approval.designer.list.noEntryWarning') }}</div>
      <div v-if="g.requested" class="text-caption text-warning" :data-testid="`flow-draft-${g.toOptionId}`">{{ t('approval.designer.list.draftTarget') }}</div>
      <div v-if="!g.flows.length" class="text-caption text-muted q-mt-xs">{{ t('approval.designer.list.noFlows') }}</div>
      <q-btn
        v-if="!readonly" flat dense no-caps color="primary" icon="add" class="q-mt-xs" :label="t('approval.designer.list.addInGroup')"
        :aria-label="t('approval.designer.list.addInGroupAria', { state: g.label })" :data-testid="`flow-add-${g.toOptionId}`" @click="openCreate(g.toOptionId)"
      />

      <q-card
        v-for="(f, i) in g.flows" :key="f.key" flat bordered class="q-mt-sm q-pa-sm" :class="{ 'flow-card--off': !f.active, 'flow-card--open': !readonly }" :data-testid="`flow-card-${f.key}`"
        :tabindex="readonly ? undefined : 0" :aria-label="readonly ? undefined : `${t('approval.designer.list.edit')} ${nameOf(f)}`" @click="openEdit(f)" @keydown.enter.self.prevent="openEdit(f)" @keydown.space.self.prevent="openEdit(f)"
        :draggable="!mobile && !readonly && !busy && g.flows.length > 1" @dragstart="dragging = { key: f.key, toOptionId: g.toOptionId }"
        @dragover.prevent @drop.prevent="onDrop(g.toOptionId, g.flows, f)" @dragend="dragging = null"
      >
        <div class="row items-start no-wrap">
          <div class="col" style="min-width: 0">
            <div class="text-body1 ellipsis-2-lines">{{ resolveI18n(f.name, locale) }}</div>
            <div class="text-caption text-muted">{{ f.key }}</div>
            <div class="text-caption q-mt-xs" style="overflow-wrap: anywhere">{{ t('approval.designer.list.condition') }}：{{ summary(f) }}</div>
            <div class="row q-gutter-xs q-mt-xs">
              <q-badge outline color="primary" :label="t('approval.designer.list.stages', { n: f.definition.stages.length })" />
              <q-badge outline color="primary" :label="t('approval.designer.list.version', { v: f.version })" />
              <q-badge outline :color="f.pendingCount ? 'warning' : 'primary'" :label="t('approval.designer.list.pending', { n: f.pendingCount })" :data-testid="`flow-pending-${f.key}`" />
              <q-badge v-if="!f.active" outline color="negative" :label="t('approval.designer.list.inactive')" :data-testid="`flow-inactive-${f.key}`" />
            </div>
          </div>
          <div v-if="!readonly" class="row no-wrap items-center q-ml-sm" @click.stop @keydown.stop>
            <q-toggle
              :model-value="f.active" :disable="busy" :aria-label="`${t('approval.designer.list.active')} ${resolveI18n(f.name, locale)}`"
              :data-testid="`flow-active-${f.key}`" @update:model-value="(v: boolean) => onToggle(f, v)"
            />
            <RowActionMenu v-if="mobile" :actions="actionsOf(g.flows, f, i)" :testid="`flow-menu-${f.key}`" :aria-label="`${t('common.moreActions')} ${nameOf(f)}`" />
            <div v-else class="row no-wrap items-center">
              <q-btn flat dense round icon="edit" :aria-label="`${t('approval.designer.list.edit')} ${nameOf(f)}`" :data-testid="`flow-edit-${f.key}`" @click="openEdit(f)" />
              <q-btn flat dense round icon="arrow_upward" :aria-label="`${t('approval.designer.list.moveUp')} ${nameOf(f)}`" :disable="busy || i === 0" :data-testid="`flow-up-${f.key}`" @click="move(g.toOptionId, g.flows, f, -1)" />
              <q-btn flat dense round icon="arrow_downward" :aria-label="`${t('approval.designer.list.moveDown')} ${nameOf(f)}`" :disable="busy || i === g.flows.length - 1" :data-testid="`flow-down-${f.key}`" @click="move(g.toOptionId, g.flows, f, 1)" />
              <q-btn
                v-if="!f.used" flat dense round icon="delete" color="negative" :aria-label="`${t('approval.designer.list.delete')} ${nameOf(f)}`" :disable="busy"
                :data-testid="`flow-delete-${f.key}`" @click="confirmingDelete = f"
              />
              <q-icon v-else name="lock" size="xs" class="q-mx-sm text-muted" :title="t('approval.designer.list.usedNoDelete')" :aria-label="t('approval.designer.list.usedNoDelete')" role="img" :data-testid="`flow-used-${f.key}`" />
            </div>
          </div>
        </div>
      </q-card>
    </section>

    <FlowEditorDialog
      v-if="dialogOpen && workflow.data.value" v-model="dialogOpen" :module="module" :workflow="workflow.data.value" :flows="flows.data.value ?? []"
      :flow="editing" :preset-target="preset" @saved="onSaved"
    />

    <q-dialog :model-value="confirmingOff !== null" @update:model-value="confirmingOff = null">
      <q-card>
        <q-card-section data-testid="flow-off-confirm-text">{{ confirmingOff?.pendingCount ? t('approval.designer.list.deactivateConfirm', { n: confirmingOff.pendingCount }) : t('approval.designer.list.deactivateConfirmNone') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('approval.designer.list.deactivate')" data-testid="flow-off-confirm" @click="confirmOff" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog :model-value="confirmingDelete !== null" @update:model-value="confirmingDelete = null">
      <q-card>
        <q-card-section>{{ t('approval.designer.list.deleteConfirm', { name: confirmingDelete ? resolveI18n(confirmingDelete.name, locale) : '' }) }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('approval.designer.list.delete')" data-testid="flow-delete-confirm" @click="confirmDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.flow-group { padding-bottom: 8px; }
.flow-group--focus { outline: 2px solid var(--q-primary); outline-offset: 4px; border-radius: 4px; }
.flow-card--open { cursor: pointer; }
.flow-card--off { opacity: 0.7; }
.flow-card--off:hover { opacity: 1; }
</style>
