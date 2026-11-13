<script setup lang="ts">
import { QCard, useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useApprovalFlows } from 'src/application/dynamic/queries/approval-flow-queries';
import { useGroups, useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useDeleteWorkflowMutation, useSaveWorkflowMutation, useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import {
  buildWorkflow, draftFromWorkflow, draftTargets, emptyWorkflowDraft, isDraftDirty, setStateEditable, setStateIncluded, setStateReadOnly,
  setTransitionApproval, withStatusField, type WorkflowDraft,
} from 'src/domain/dynamic/workflow';
import { fieldOptions } from 'src/presentation/dynamic/designer/field-options';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ module: ModuleDetail }>();
const { t, locale } = useI18n();
const $q = useQuasar();
// Phone width swaps the state table and the transition rows for cards: five columns do not fit 390px.
const mobile = computed(() => $q.screen.lt.sm);

const key = computed(() => props.module.key);
const query = useWorkflowQuery(key);
const unsupported = computed(() => !!props.module.detailOf || props.module.kind === 'extension');
const flows = useApprovalFlows(key, () => !unsupported.value);
const groups = useGroups();
const me = useMe();
const save = useSaveWorkflowMutation(key);
const remove = useDeleteWorkflowMutation(key);

const draft = ref<WorkflowDraft | null>(null);
// General lines, and the messages that belong to one row (key as the backend sends it: transitions[a>b], states[id], initialOptionId).
const errors = ref<string[]>([]);
const rowErrors = ref<Record<string, string[]>>({});
const clearErrors = () => { errors.value = []; rowErrors.value = {}; };
// The server response (locks, candidates) and the draft are separate: a refetch only replaces the draft when nothing is unsaved,
// otherwise a flow saved on another tab would wash out the toggle the designer just turned on. Saving resets it explicitly.
// query.data is null when the module has no workflow; a module switch always drops the previous module's draft.
let loadedKey = '';
watch([key, () => query.data.value], ([k, d], [, oldD]) => {
  const switched = k !== loadedKey;
  loadedKey = k;
  if (d === undefined) { draft.value = null; clearErrors(); return; }
  if (!switched && draft.value && isDraftDirty(draft.value, oldD)) return;
  draft.value = draftFromWorkflow(d);
  clearErrors();
}, { immediate: true });

const readonly = computed(() => !me.data.value?.designer.edit);
const ready = computed(() => draft.value && groups.data.value && me.data.value);
const exists = computed(() => !!query.data.value);

const flowCount = (toOptionId: number | null): number | undefined =>
  toOptionId == null || !flows.data.value ? undefined : flows.data.value.filter((f) => f.toOptionId === toOptionId).length;
const optionLabel = (id: number) => stateOptions.value.find((o) => o.value === id)?.label ?? String(id);
// Transitions that need approval into a state that has no flow would be rejected by the server; say why before the click.
const noFlowTargets = computed(() => (draft.value ? draftTargets(draft.value).filter((id) => flowCount(id) === 0) : []));

const statusFieldOptions = computed(() => fieldOptions(props.module, locale.value, (f) => f.type === 'enum'));
const statusField = computed(() => props.module.blocks.flatMap((b) => b.fields).find((f) => f.id === draft.value?.statusFieldId));
const stateOptions = computed(() => (statusField.value?.options ?? []).filter((o) => o.active)
  .map((o) => ({ value: o.id, label: resolveI18n(o.label, locale.value) })));
const includedOptions = computed(() => stateOptions.value.filter((o) => draft.value!.states.some((s) => s.optionId === o.value)));
const groupOptions = computed(() => (groups.data.value ?? []).map((g) => ({ value: g.id, label: resolveI18n(g.name, locale.value) })));

const statusSel = useSelectFilter(statusFieldOptions);
const fromSel = useSelectFilter<{ value: number; label: string }>([]);
const toSel = useSelectFilter<{ value: number; label: string }>([]);
const groupSel = useSelectFilter<{ value: number; label: string }>([]);

const isIncluded = (id: number) => draft.value!.states.some((s) => s.optionId === id);
const isReadOnly = (id: number) => !!draft.value!.states.find((s) => s.optionId === id)?.readOnly;
const patch = (p: Partial<WorkflowDraft>) => { draft.value = { ...draft.value!, ...p }; clearErrors(); };
const edit = (next: WorkflowDraft) => { draft.value = next; clearErrors(); };

const setApproval = (i: number, on: boolean) => {
  const next = setTransitionApproval(draft.value!, i, on ? { required: true, noMatch: 'block' } : null);
  const to = draft.value!.transitions[i]?.to;
  // an approval target is locked, never editable: drop the flag so the save body does not contradict itself
  edit(on && to != null ? setStateEditable(next, to, false) : next);
};
const setNoMatch = (i: number, noMatch: 'block' | 'direct') => edit(setTransitionApproval(draft.value!, i, { required: true, noMatch }));
const noMatchOptions = computed(() => [
  { value: 'block', label: t('docflow.workflow.approval.noMatchBlock') },
  { value: 'direct', label: t('docflow.workflow.approval.noMatchDirect') },
]);

const setTransition = (i: number, p: Partial<WorkflowDraft['transitions'][number]>) =>
  patch({ transitions: draft.value!.transitions.map((x, j) => (j === i ? { ...x, ...p } : x)) });
const addTransition = () => patch({ transitions: [...draft.value!.transitions, { from: null, to: null, groupIds: [] }] });
const removeTransition = (i: number) => patch({ transitions: draft.value!.transitions.filter((_, j) => j !== i) });
// A transition that needs approval cannot be edited here; dropping it silently would let users skip the approval.
const confirmingTransition = ref<number | null>(null);
const askRemoveTransition = (i: number) => {
  if (draft.value!.transitions[i]?.approval?.required) confirmingTransition.value = i;
  else removeTransition(i);
};
function confirmRemoveTransition() {
  if (confirmingTransition.value !== null) removeTransition(confirmingTransition.value);
  confirmingTransition.value = null;
}
const savedOf = (k: 'lockedStates' | 'editableStates' | 'lockedFieldIds') => query.data.value?.[k] ?? [];
const isLocked = (id: number) => savedOf('lockedStates').includes(id);
const isEditable = (id: number) => savedOf('editableStates').includes(id);
const isInitial = (id: number) => draft.value!.initialOptionId === id;
const isApprovalTarget = (id: number) => draftTargets(draft.value!).includes(id);
// Shown state of the "editable" toggle; the initial state is always editable and an approval target is locked, so neither is a free choice.
const editableShown = (id: number) => isInitial(id) || (!isApprovalTarget(id) && !!draft.value!.states.find((s) => s.optionId === id)?.editable);
const editableReason = (id: number) => (isInitial(id) ? t('docflow.workflow.lock.initialEditable')
  : isApprovalTarget(id) ? t('docflow.workflow.lock.targetLocked') : null);
const dirty = computed(() => !!draft.value && isDraftDirty(draft.value, query.data.value));
const lockedFieldNames = computed(() => {
  const ids = new Set(savedOf('lockedFieldIds'));
  return props.module.blocks.flatMap((b) => b.fields).filter((f) => ids.has(f.id)).map((f) => resolveI18n(f.label, locale.value));
});
const hasApproval = computed(() => !!draft.value && draftTargets(draft.value).length > 0);

// An approval target is locked, never editable; whichever way the target got chosen, the body must not say otherwise.
const body = computed(() => (draft.value
  ? buildWorkflow(draftTargets(draft.value).reduce((d, id) => setStateEditable(d, id, false), draft.value)) : undefined));

const rowKey = /^(transitions\[\d+>\d+\]|states\[\d+\]|initialOptionId)$/;
const rowShown = (k: string) => {
  const state = /^states\[(\d+)\]$/.exec(k)?.[1];
  if (state) return stateOptions.value.some((o) => o.value === Number(state));
  return !k.startsWith('transitions[') || draft.value!.transitions.some((x) => `transitions[${x.from}>${x.to}]` === k);
};
const flowKey = /^flows\[(.+)\]$/;
function fail(e: unknown) {
  const err = e as ApiError;
  // a 400 carries per-key messages and a generic title; the title only helps when there is nothing else
  clearErrors();
  const rows: Record<string, string[]> = {};
  const lines: string[] = [];
  for (const [k, msgs] of Object.entries(err.errors ?? {})) {
    const flow = flowKey.exec(k)?.[1];
    // a message whose row is not on screen (inactive option, removed transition) goes to the list instead of vanishing
    if (rowKey.test(k) && rowShown(k)) rows[k] = msgs;
    else if (rowKey.test(k)) lines.push(...msgs.map((m) => `${k}：${m}`));
    else if (flow) {
      const f = flows.data.value?.find((x) => x.key === flow);
      lines.push(...msgs.map((m) => `${f ? resolveI18n(f.name, locale.value) : flow}：${m}`));
    } else lines.push(...msgs);
  }
  rowErrors.value = rows;
  errors.value = lines;
  if (!lines.length && !Object.keys(rows).length && err.message) errors.value = [err.message];
  if (!errors.value.length && !Object.keys(rows).length) $q.notify({ type: 'negative', message: t('designer.saveFailed') });
}
const transitionError = (tr: WorkflowDraft['transitions'][number]) => rowErrors.value[`transitions[${tr.from}>${tr.to}]`];
const initialError = computed(() => rowErrors.value.initialOptionId);
const stateError = (id: number) => rowErrors.value[`states[${id}]`];
function submit() {
  if (!body.value) return;
  clearErrors();
  save.mutate(body.value, {
    onSuccess: (data) => {
      if (data) draft.value = draftFromWorkflow(data);
      $q.notify({ type: 'positive', message: t('docflow.workflow.saved') });
    },
    onError: fail,
  });
}
const confirmingRemove = ref(false);
function removeWorkflow() {
  clearErrors();
  confirmingRemove.value = false;
  remove.mutate(undefined, {
    onSuccess: () => { draft.value = emptyWorkflowDraft(); $q.notify({ type: 'positive', message: t('docflow.workflow.removed') }); },
    onError: fail,
  });
}
</script>

<template>
  <div v-if="unsupported" class="text-muted" data-testid="workflow-unsupported">{{ t('docflow.workflow.unsupported') }}</div>
  <div v-else-if="query.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="!ready" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else class="q-gutter-md" data-testid="workflow-tab">
    <div class="text-caption text-muted">{{ t('docflow.workflow.hint') }}</div>

    <q-select
      :model-value="draft!.statusFieldId" :options="statusSel.filtered.value" :label="t('docflow.workflow.statusField')"
      :hint="statusFieldOptions.length ? t('docflow.workflow.statusFieldHint') : t('docflow.workflow.noEnum')" :disable="readonly"
      emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="workflow-status-field"
      @filter="statusSel.onFilter" @popup-hide="statusSel.reset" @update:model-value="(v: number | null) => edit(withStatusField(draft!, v))"
    >
      <template #no-option><SelectNoOption /></template>
    </q-select>

    <template v-if="statusField">
      <div>
        <div class="text-subtitle1">{{ t('docflow.workflow.states') }}</div>
        <div class="text-caption text-muted q-mb-xs">{{ t('docflow.workflow.statesHint') }}</div>
        <div v-if="dirty && exists" class="text-caption text-muted q-mb-xs" data-testid="lock-dirty-caption">{{ t('docflow.workflow.lock.dirtyCaption') }}</div>
        <q-markup-table v-if="!mobile" flat bordered dense wrap-cells>
          <thead>
            <tr>
              <th class="text-left">{{ t('docflow.workflow.state') }}</th>
              <th>{{ t('docflow.workflow.included') }}</th>
              <th>{{ t('docflow.workflow.initial') }}</th>
              <th>{{ t('docflow.workflow.readOnly') }}</th>
              <th>{{ t('docflow.workflow.lock.editableColumn') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="o in stateOptions" :key="o.value" :data-testid="`state-row-${o.value}`">
              <td class="text-left">
                {{ o.label }}
                <q-badge v-if="isLocked(o.value)" color="warning" text-color="dark" class="q-ml-xs" :data-testid="`state-locked-${o.value}`">
                  <q-icon name="lock" size="xs" class="q-mr-xs" />{{ t('docflow.workflow.lock.locked') }}
                </q-badge>
                <q-badge v-if="isEditable(o.value)" outline color="primary" class="q-ml-xs" :label="t('docflow.workflow.editableState')" :data-testid="`state-editable-${o.value}`" />
              </td>
              <td class="text-center">
                <q-checkbox
                  :model-value="isIncluded(o.value)" dense :disable="readonly" :data-testid="`state-included-${o.value}`"
                  @update:model-value="(v: boolean) => edit(setStateIncluded(draft!, o.value, v))"
                />
              </td>
              <td class="text-center">
                <q-radio
                  :model-value="draft!.initialOptionId" :val="o.value" dense :disable="readonly || !isIncluded(o.value)"
                  :data-testid="`state-initial-${o.value}`" @update:model-value="(v: number) => patch({ initialOptionId: v })"
                />
              </td>
              <td class="text-center">
                <q-checkbox
                  :model-value="isReadOnly(o.value)" dense :disable="readonly || !isIncluded(o.value)" :data-testid="`state-readonly-${o.value}`"
                  @update:model-value="(v: boolean) => edit(setStateReadOnly(draft!, o.value, v))"
                />
              </td>
              <td class="text-center">
                <q-checkbox
                  :model-value="editableShown(o.value)" dense :aria-label="t('docflow.workflow.lock.editableColumn')"
                  :disable="readonly || !isIncluded(o.value) || editableReason(o.value) !== null" :data-testid="`state-editable-toggle-${o.value}`"
                  @update:model-value="(v: boolean) => edit(setStateEditable(draft!, o.value, v))"
                />
                <div v-if="isIncluded(o.value) && editableReason(o.value)" class="text-caption text-muted" :data-testid="`state-editable-reason-${o.value}`">{{ editableReason(o.value) }}</div>
                <div v-for="m in stateError(o.value) ?? []" :key="m" class="text-caption text-negative" :data-testid="`state-error-${o.value}`">{{ m }}</div>
              </td>
            </tr>
            <tr v-if="!stateOptions.length"><td colspan="5" class="text-muted">{{ t('docflow.workflow.noOptions') }}</td></tr>
          </tbody>
        </q-markup-table>
        <div v-else class="column no-wrap q-gutter-y-sm" data-testid="state-cards">
          <q-card v-for="o in stateOptions" :key="o.value" flat bordered class="q-pa-sm" :data-testid="`state-row-${o.value}`">
            <div class="row items-center q-gutter-xs">
              <span class="text-weight-medium">{{ o.label }}</span>
              <q-badge v-if="isLocked(o.value)" color="warning" text-color="dark" :data-testid="`state-locked-${o.value}`">
                <q-icon name="lock" size="xs" class="q-mr-xs" />{{ t('docflow.workflow.lock.locked') }}
              </q-badge>
              <q-badge v-if="isEditable(o.value)" outline color="primary" :label="t('docflow.workflow.editableState')" :data-testid="`state-editable-${o.value}`" />
            </div>
            <div class="column no-wrap">
              <q-checkbox
                :model-value="isIncluded(o.value)" dense :label="t('docflow.workflow.included')" :disable="readonly" :data-testid="`state-included-${o.value}`"
                @update:model-value="(v: boolean) => edit(setStateIncluded(draft!, o.value, v))"
              />
              <q-radio
                :model-value="draft!.initialOptionId" :val="o.value" dense :label="t('docflow.workflow.initial')" :disable="readonly || !isIncluded(o.value)"
                :data-testid="`state-initial-${o.value}`" @update:model-value="(v: number) => patch({ initialOptionId: v })"
              />
              <q-checkbox
                :model-value="isReadOnly(o.value)" dense :label="t('docflow.workflow.readOnly')" :disable="readonly || !isIncluded(o.value)" :data-testid="`state-readonly-${o.value}`"
                @update:model-value="(v: boolean) => edit(setStateReadOnly(draft!, o.value, v))"
              />
              <q-checkbox
                :model-value="editableShown(o.value)" dense :label="t('docflow.workflow.lock.editableColumn')"
                :disable="readonly || !isIncluded(o.value) || editableReason(o.value) !== null" :data-testid="`state-editable-toggle-${o.value}`"
                @update:model-value="(v: boolean) => edit(setStateEditable(draft!, o.value, v))"
              />
            </div>
            <div v-if="isIncluded(o.value) && editableReason(o.value)" class="text-caption text-muted" :data-testid="`state-editable-reason-${o.value}`">{{ editableReason(o.value) }}</div>
            <div v-for="m in stateError(o.value) ?? []" :key="m" class="text-caption text-negative" :data-testid="`state-error-${o.value}`">{{ m }}</div>
          </q-card>
          <div v-if="!stateOptions.length" class="text-muted">{{ t('docflow.workflow.noOptions') }}</div>
        </div>
        <div v-if="initialError" class="text-caption text-negative q-mt-xs" data-testid="initial-error">
          <div v-for="m in initialError" :key="m">{{ m }}</div>
        </div>
        <div v-if="lockedFieldNames.length" class="text-caption q-mt-xs" data-testid="locked-fields">
          {{ t('docflow.workflow.lock.lockedFields', { names: lockedFieldNames.join('、') }) }}
        </div>
        <div v-if="hasApproval || savedOf('lockedStates').length" class="text-caption text-muted q-mt-xs" data-testid="readonly-suggestion">
          {{ t('docflow.workflow.lock.readOnlySuggestion') }}
        </div>
      </div>

      <div>
        <div class="row items-center">
          <div class="text-subtitle1">{{ t('docflow.workflow.transitions') }}</div>
          <q-space />
          <q-btn
            dense color="primary" icon="add" :label="t('docflow.workflow.addTransition')" :disable="readonly || includedOptions.length < 2"
            data-testid="add-transition" @click="addTransition"
          />
        </div>
        <div class="text-caption text-muted q-mb-xs">{{ t('docflow.workflow.groupsHint') }}</div>
        <div v-if="!draft!.transitions.length" class="text-muted">{{ t('docflow.workflow.noTransitions') }}</div>
        <component
          :is="mobile ? QCard : 'div'" v-for="(tr, i) in draft!.transitions" :key="i" v-bind="mobile ? { flat: true, bordered: true } : {}"
          class="row no-wrap items-start q-mb-sm" :class="{ 'row-error': transitionError(tr), 'q-pa-sm': mobile }" :data-testid="`transition-row-${i}`"
        >
          <div class="col column no-wrap q-gutter-y-xs">
            <div class="row items-start q-col-gutter-sm">
              <q-select
                class="col-12 col-sm-5 col-md" :model-value="tr.from" :options="fromSel.filterBy(includedOptions)" :label="t('docflow.workflow.from')" :disable="readonly"
                emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="`transition-from-${i}`"
                @filter="fromSel.onFilter" @popup-hide="fromSel.reset" @update:model-value="(v: number) => setTransition(i, { from: v })"
              >
                <template #no-option><SelectNoOption /></template>
              </q-select>
              <q-icon name="arrow_forward" class="gt-xs q-mt-md" />
              <q-select
                class="col-12 col-sm-5 col-md" :model-value="tr.to" :options="toSel.filterBy(includedOptions)" :label="t('docflow.workflow.to')" :disable="readonly"
                emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="`transition-to-${i}`"
                @filter="toSel.onFilter" @popup-hide="toSel.reset" @update:model-value="(v: number) => setTransition(i, { to: v })"
              >
                <template #no-option><SelectNoOption /></template>
              </q-select>
              <q-select
                class="col-12 col-md-4" :model-value="tr.groupIds" :options="groupSel.filterBy(groupOptions)" :label="t('docflow.workflow.groups')"
                :hint="tr.groupIds.length ? undefined : t('docflow.workflow.anyoneWithEdit')" :disable="readonly"
                multiple use-chips emit-value map-options outlined dense use-input input-debounce="0" :data-testid="`transition-groups-${i}`"
                @filter="groupSel.onFilter" @popup-hide="groupSel.reset" @update:model-value="(v: number[]) => setTransition(i, { groupIds: v })"
              >
                <template #no-option><SelectNoOption /></template>
              </q-select>
            </div>
            <div class="row items-center q-col-gutter-sm">
              <div class="col-12 col-sm-auto">
                <q-toggle
                  :model-value="!!tr.approval?.required" :label="t('docflow.workflow.approval.toggle')" :aria-label="t('docflow.workflow.approval.toggleAria')"
                  :disable="readonly" dense :data-testid="`transition-approval-toggle-${i}`" @update:model-value="(v: boolean) => setApproval(i, v)"
                />
              </div>
              <template v-if="tr.approval?.required">
                <q-select
                  class="col-12 col-sm-4" :model-value="tr.approval.noMatch" :options="noMatchOptions" :label="t('docflow.workflow.approval.noMatch')"
                  :disable="readonly" emit-value map-options outlined dense :data-testid="`transition-nomatch-${i}`"
                  @update:model-value="(v: 'block' | 'direct') => setNoMatch(i, v)"
                />
                <div v-if="tr.to != null && flowCount(tr.to) !== undefined" class="col-12 col-sm-auto">
                  <span class="text-caption text-muted" :data-testid="`transition-flows-${i}`">{{ t('docflow.workflow.approval.flowCount', { n: flowCount(tr.to) }) }}</span>
                </div>
              </template>
            </div>
            <div v-for="m in transitionError(tr) ?? []" :key="m" class="text-caption text-negative" :data-testid="`transition-error-${i}`">{{ m }}</div>
            <div v-if="tr.approval?.required && tr.approval.noMatch === 'direct'" class="text-caption text-warning" :data-testid="`transition-direct-warning-${i}`">
              {{ t('docflow.workflow.approval.directWarning') }}
            </div>
          </div>
          <q-btn flat dense round icon="delete" color="negative" :title="t('docflow.workflow.removeTransition')" :aria-label="t('docflow.workflow.removeTransition')" :disable="readonly" :data-testid="`transition-remove-${i}`" @click="askRemoveTransition(i)" />
        </component>
      </div>
    </template>

    <div v-if="noFlowTargets.length" class="text-warning" data-testid="workflow-no-flow">
      <div v-for="id in noFlowTargets" :key="id">{{ t('docflow.workflow.approval.noFlowReason', { state: optionLabel(id) }) }}</div>
    </div>

    <div v-if="errors.length" class="text-negative" data-testid="workflow-errors">
      <div v-for="line in errors" :key="line">{{ line }}</div>
    </div>

    <div class="row justify-end q-gutter-sm">
      <q-btn
        v-if="exists" outline color="negative" :label="t('docflow.workflow.remove')" :disable="readonly" :loading="remove.isPending.value"
        data-testid="remove-workflow" @click="confirmingRemove = true"
      />
      <q-btn
        color="primary" :label="t('docflow.workflow.save')" :disable="readonly || !body || noFlowTargets.length > 0" :loading="save.isPending.value"
        data-testid="save-workflow" @click="submit"
      />
    </div>

    <q-dialog :model-value="confirmingTransition !== null" @update:model-value="confirmingTransition = null">
      <q-card>
        <q-card-section>{{ t('docflow.workflow.removeApprovalConfirm') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('docflow.workflow.removeTransition')" data-testid="confirm-remove-transition" @click="confirmRemoveTransition" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog v-model="confirmingRemove">
      <q-card>
        <q-card-section>{{ t('docflow.workflow.removeConfirm') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
          <q-btn color="negative" :label="t('docflow.workflow.remove')" data-testid="confirm-remove-workflow" @click="removeWorkflow" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.row-error { outline: 1px solid var(--q-negative); outline-offset: 2px; border-radius: 4px; }
</style>
