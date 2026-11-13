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
import { groupFlows, moveKeys, moveKeyTo } from './flow-groups';

const props = defineProps<{ module: ModuleDetail; focusTarget?: number | null }>();
// The editing dialog arrives with a4a4; these are declared so the page can already wire them.
defineEmits<{ edit: [flow: ApprovalFlow]; create: [toOptionId: number] }>();
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
const groups = computed(() => groupFlows(flows.data.value ?? [], workflow.data.value ?? undefined, optionLabel));
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
  if (!keys || readonly.value) return;
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

const actionsOf = (list: ApprovalFlow[], f: ApprovalFlow, i: number) => [
  { label: t('approval.designer.list.moveUp'), run: () => move(f.toOptionId, list, f, -1), disable: i === 0, testid: `flow-up-${f.key}` },
  { label: t('approval.designer.list.moveDown'), run: () => move(f.toOptionId, list, f, 1), disable: i === list.length - 1, testid: `flow-down-${f.key}` },
  ...(f.used ? [] : [{ label: t('approval.designer.list.delete'), run: () => { confirmingDelete.value = f; }, negative: true, testid: `flow-delete-${f.key}` }]),
];
</script>

<template>
  <div v-if="unsupported" class="text-muted">{{ t('docflow.workflow.unsupported') }}</div>
  <div v-else-if="workflow.isError.value || flows.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="workflow.isPending.value || flows.isPending.value" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else-if="workflow.data.value === null" class="text-muted" data-testid="flows-no-workflow">{{ t('approval.designer.list.noWorkflow') }}</div>
  <div v-else class="q-gutter-md" data-testid="flows-tab">
    <div class="text-caption text-muted">{{ t('approval.designer.list.hint') }}</div>

    <section
      v-for="g in groups" :key="g.toOptionId" :class="{ 'flow-group--focus': highlighted === g.toOptionId }" class="flow-group"
      role="group" :aria-label="t('approval.designer.list.groupAria', { state: g.label })" :data-testid="`flow-group-${g.toOptionId}`"
    >
      <div class="text-subtitle1 text-weight-medium">{{ g.label }}</div>
      <div v-if="g.entries.length" class="text-caption text-muted" :data-testid="`flow-entries-${g.toOptionId}`">
        {{ t('approval.designer.list.entries', { entries: g.entries.join('、') }) }}
      </div>
      <div v-else-if="g.flows.length" class="text-caption text-warning" :data-testid="`flow-no-entry-${g.toOptionId}`">{{ t('approval.designer.list.noEntryWarning') }}</div>
      <div v-if="!g.flows.length" class="text-caption text-muted q-mt-xs">{{ t('approval.designer.list.noFlows') }}</div>

      <q-card
        v-for="(f, i) in g.flows" :key="f.key" flat bordered class="q-mt-sm q-pa-sm" :class="{ 'flow-card--off': !f.active }" :data-testid="`flow-card-${f.key}`"
        :draggable="!mobile && !readonly && g.flows.length > 1" @dragstart="dragging = { key: f.key, toOptionId: g.toOptionId }"
        @dragover.prevent @drop.prevent="onDrop(g.toOptionId, g.flows, f)" @dragend="dragging = null"
      >
        <div class="row items-start no-wrap q-gutter-x-sm">
          <div class="col" style="min-width: 0">
            <div class="text-body1 ellipsis-2-lines">{{ resolveI18n(f.name, locale) }}</div>
            <div class="text-caption text-muted">{{ f.key }}</div>
            <div class="text-caption q-mt-xs">{{ t('approval.designer.list.condition') }}：{{ summary(f) }}</div>
            <div class="row q-gutter-xs q-mt-xs">
              <q-badge outline color="primary" :label="t('approval.designer.list.stages', { n: f.definition.stages.length })" />
              <q-badge outline color="primary" :label="t('approval.designer.list.version', { v: f.version })" />
              <q-badge outline :color="f.pendingCount ? 'warning' : 'primary'" :label="t('approval.designer.list.pending', { n: f.pendingCount })" :data-testid="`flow-pending-${f.key}`" />
              <q-badge v-if="!f.active" outline color="negative" :label="t('approval.designer.list.inactive')" :data-testid="`flow-inactive-${f.key}`" />
            </div>
          </div>
          <template v-if="!readonly">
            <q-toggle
              :model-value="f.active" :disable="busy" :aria-label="`${t('approval.designer.list.active')} ${resolveI18n(f.name, locale)}`"
              :data-testid="`flow-active-${f.key}`" @update:model-value="(v: boolean) => onToggle(f, v)"
            />
            <RowActionMenu v-if="mobile" :actions="actionsOf(g.flows, f, i)" :testid="`flow-menu-${f.key}`" />
            <div v-else class="row no-wrap items-center">
              <q-btn flat dense round icon="arrow_upward" :aria-label="t('approval.designer.list.moveUp')" :disable="busy || i === 0" :data-testid="`flow-up-${f.key}`" @click="move(g.toOptionId, g.flows, f, -1)" />
              <q-btn flat dense round icon="arrow_downward" :aria-label="t('approval.designer.list.moveDown')" :disable="busy || i === g.flows.length - 1" :data-testid="`flow-down-${f.key}`" @click="move(g.toOptionId, g.flows, f, 1)" />
              <q-btn
                v-if="!f.used" flat dense round icon="delete" color="negative" :aria-label="t('approval.designer.list.delete')" :disable="busy"
                :data-testid="`flow-delete-${f.key}`" @click="confirmingDelete = f"
              />
              <q-icon v-else name="lock" size="xs" class="q-mx-sm text-muted" :title="t('approval.designer.list.usedNoDelete')" :aria-label="t('approval.designer.list.usedNoDelete')" role="img" :data-testid="`flow-used-${f.key}`" />
            </div>
          </template>
        </div>
      </q-card>
    </section>

    <q-dialog :model-value="confirmingOff !== null" @update:model-value="confirmingOff = null">
      <q-card>
        <q-card-section data-testid="flow-off-confirm-text">{{ t('approval.designer.list.deactivateConfirm', { n: confirmingOff?.pendingCount ?? 0 }) }}</q-card-section>
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
.flow-card--off { opacity: 0.7; }
.flow-card--off:hover { opacity: 1; }
</style>
