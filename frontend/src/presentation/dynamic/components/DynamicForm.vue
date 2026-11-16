<script setup lang="ts">
import { toTypedSchema } from '@vee-validate/zod';
import { useForm } from 'vee-validate';
import { computed, onMounted, provide, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { fillPatch, fillReferenceInfos, lockedFillKeys } from 'src/domain/dynamic/fill';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { approvalTargets, canAdvance, intersectAllowed, isReadOnlyState, selectableStates } from 'src/domain/dynamic/workflow';
import type { DetailRowPayload, DynamicRecord, ModuleSchema, ReferenceInfo, SchemaBlock, SchemaField } from 'src/domain/dynamic/types';
import { joinList } from 'src/i18n/translate';
import { attachmentContextKey } from 'src/presentation/dynamic/attachment-context';
import { buildZodSchema } from 'src/presentation/dynamic/build-zod-schema';
import DetailGrid from 'src/presentation/dynamic/details/DetailGrid.vue';
import { fieldColClass, isFullWidth } from 'src/presentation/dynamic/form-layout';
import { buildDetailPayload, rowErrors, toRowStates, validateRows, type DetailRowState } from 'src/presentation/dynamic/details/detail-rows';
import { defaultValueOf } from 'src/presentation/dynamic/field-type-registry';
import { isWritable, ruleStateOf } from 'src/presentation/dynamic/rule-state';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DynamicField from './DynamicField.vue';

const props = defineProps<{
  schema: ModuleSchema;
  mode: 'create' | 'edit';
  record?: DynamicRecord;
  /** Create mode: initial values that override the defaults (e.g. reference prefill). */
  initial?: Record<string, unknown>;
  /** Create mode: reference info for `initial` values (e.g. the prefilled target's title). */
  references?: Record<string, ReferenceInfo>;
  serverErrors?: Record<string, string[]>;
  /** Server errors keyed `details.{childKey}[{i}]...`, `i` indexing the last submitted payload. */
  detailErrors?: Record<string, string[]>;
  saving?: boolean;
}>();
const emit = defineEmits<{
  submit: [values: Record<string, unknown>, details: Record<string, DetailRowPayload[]>];
  /** An attachment upload saved the record on its own; the page must send this rowVersion with the next save. */
  rowVersion: [rowVersion: string];
}>();
// Attachments can only be uploaded to a saved record, so the context exists only in edit mode.
provide(attachmentContextKey, {
  get recordId() { return props.mode === 'edit' ? (props.record?.id ?? null) : null; },
  onUploaded: (rowVersion) => emit('rowVersion', rowVersion),
});
const { t, locale } = useI18n();

const fields = computed(() => props.schema.blocks.flatMap((b) => b.fields));

// Workflow: the status field starts at the initial state on create; later the options and the form follow the stored state.
const workflow = computed(() => props.schema.workflow);
const statusKey = computed(() => workflow.value?.statusFieldKey);

// A contributed field whose app is closed is frozen for everyone; the server rejects any different value with 409.
const lockOf = (f: SchemaField) => f.lockedBy ?? props.record?.lockedBy?.[f.key] ?? null;

function initialValues(): Record<string, unknown> {
  return Object.fromEntries(fields.value.map((f) => [
    f.key,
    (props.mode === 'edit' ? props.record?.values[f.key]
      : lockOf(f) ? null // a frozen field gets no default on create
      : (f.key === props.schema.workflow?.statusFieldKey ? props.schema.workflow.initialState : (props.initial?.[f.key] ?? defaultValueOf(f)))) ?? null,
  ]));
}

let snapshot = initialValues();

// Detail rows live outside vee-validate; edit mode starts from the record's rows.
const initialRows = () => Object.fromEntries((props.schema.details ?? []).map((d) => [
  d.moduleKey, toRowStates(props.mode === 'edit' ? props.record?.details?.[d.moduleKey] : undefined),
]));
const detailRows = ref<Record<string, DetailRowState[]>>(initialRows());
/** Per detail module, the row uid of each item in the last submitted payload; FormPage maps server errors with it. */
const uidAt = ref<Record<string, string[]>>({});
const rowsTried = ref(false); // client row errors show after the first submit, then follow the edits
const clientRowErrors = computed(() => Object.fromEntries((props.schema.details ?? []).map((d) => [
  d.moduleKey, rowsTried.value ? validateRows(d, detailRows.value[d.moduleKey] ?? [], locale.value, t) : new Map<string, Record<string, string>>(),
])));
// Server row errors, per detail module; a cell's error is dropped once the user edits it (`_record` stays until the next submit).
const serverRowErrors = ref<Record<string, Map<string, Record<string, string>>>>({});
watch(() => props.detailErrors, (errs) => {
  serverRowErrors.value = Object.fromEntries((props.schema.details ?? []).map((d) => [d.moduleKey, rowErrors(errs, d.moduleKey, uidAt.value[d.moduleKey] ?? [])]));
});
function clearServerCell(childKey: string, uid: string, fieldKey: string) {
  const current = serverRowErrors.value[childKey];
  const row = current?.get(uid);
  if (!row || !(fieldKey in row)) return;
  const { [fieldKey]: _dropped, ...rest } = row;
  const next = new Map(current);
  if (Object.keys(rest).length) next.set(uid, rest);
  else next.delete(uid);
  serverRowErrors.value = { ...serverRowErrors.value, [childKey]: next };
}
// Merged per field: a client error on a row must not hide that row's server errors.
function gridErrors(childKey: string) {
  const out = new Map(serverRowErrors.value[childKey]);
  for (const [uid, errs] of clientRowErrors.value[childKey] ?? []) out.set(uid, { ...out.get(uid), ...errs });
  return out;
}
defineExpose({ uidAt });
// Declared before useForm so the schema computed can read it; filled right after.
const formValues = ref<Record<string, unknown>>(snapshot);

// ReadOnly rules look at the loaded record only (decision 4); the rest follow the live form values.
const stored = computed(() => (props.mode === 'edit' ? props.record?.values : undefined));
const ruleState = computed(() => ruleStateOf(props.schema, formValues.value, stored.value, { skipComputed: true }));
const storedRuleState = computed(() => stored.value && ruleStateOf(props.schema, stored.value, stored.value, { skipComputed: true }));

const { handleSubmit, values, errors, setErrors, setFieldValue, resetForm } = useForm({
  // Identity `cast`: vee-validate would otherwise rewrite the model with zod-normalized values (e.g. trimmed text).
  validationSchema: computed(() => ({
    ...toTypedSchema(buildZodSchema(props.schema, props.mode, props.record, ruleState.value, {
      storedRuleState: storedRuleState.value, locale: locale.value, t,
    })),
    cast: (v: unknown) => v,
  })),
  initialValues: snapshot,
  // Rule-hidden fields unmount but keep their value; a changed one is still submitted (decision 8).
  keepValuesOnUnmount: true,
});
watch(values, (v) => { formValues.value = v as Record<string, unknown>; }, { deep: true, immediate: true });

const storedStatus = computed(() => (props.mode === 'edit' && statusKey.value ? props.record?.values[statusKey.value] : undefined));
const statusAllowed = computed(() => selectableStates(workflow.value, props.mode, storedStatus.value));
// A read-only state locks everything but the status field (and it only matters when the user can still advance it).
// A record an event has already posted is locked the same way: only the status may change (the cancel flow).
const frozen = computed(() => props.mode === 'edit' && !!props.record?.frozenByEffects);
const workflowLocked = computed(() => props.mode === 'edit' && (frozen.value || isReadOnlyState(workflow.value, storedStatus.value)));
// States reachable only through "submit approval": tell the user where to go instead of leaving them out of the list unexplained.
const approvalHint = computed(() => {
  const status = fields.value.find((f) => f.key === statusKey.value);
  // A target whose label the viewer cannot see would read as a blank name: leave it out.
  const labels = approvalTargets(workflow.value, storedStatus.value)
    .map((v) => resolveI18n(status?.options.find((o) => o.value === v)?.label, locale.value)).filter(Boolean);
  return props.mode === 'edit' && labels.length
    ? t('approval.useSubmitHint', { states: joinList(labels, locale.value) })
    : '';
});
const allowedFor = (f: SchemaField) => {
  const byRules = ruleState.value.allowedOptions.get(f.id);
  return f.key === statusKey.value ? intersectAllowed(byRules, statusAllowed.value) : byRules;
};

// Fill: after a reference with a `fill` config changes, copy values of the picked record into the form (a preview; the server recomputes).
const lockedKeys = computed(() => lockedFillKeys(fields.value));
const fillFields = computed(() => fields.value.filter((f) => f.type === 'reference' && f.reference?.kind === 'module' && f.config.fill?.map.length));
const appliedRef: Record<string, unknown> = Object.fromEntries(fillFields.value.map((f) => [f.key, (props.mode === 'edit' ? snapshot[f.key] : null) ?? null]));
const fillReferences = ref<Record<string, ReferenceInfo>>({});
async function syncFill() {
  for (const f of fillFields.value) {
    const now = values[f.key] ?? null;
    if (JSON.stringify(now) === JSON.stringify(appliedRef[f.key])) continue;
    appliedRef[f.key] = now;
    let source: DynamicRecord | undefined;
    if (now !== null) {
      try { source = await dynamicApi.getRecord(Number(now)); } catch { continue; } // the server judges on save
      if (JSON.stringify(values[f.key] ?? null) !== JSON.stringify(now)) continue; // the user picked another one meanwhile
    }
    const patch = fillPatch(f.config.fill!, source?.values, values as Record<string, unknown>, props.mode === 'edit' ? snapshot : undefined);
    for (const [key, value] of Object.entries(patch)) setFieldValue(key, value);
    fillReferences.value = { ...fillReferences.value, ...fillReferenceInfos(f.config.fill!, patch, source?.references) };
  }
}
watch(() => fillFields.value.map((f) => values[f.key] ?? null), syncFill);
onMounted(syncFill); // a prefilled reference on create

const hiddenField = (f: SchemaField) => ruleState.value.hidden.has(f.id);
// After an approval the server locks these fields (and the detail rows); the status field is never on the list, so unlocking transitions stay possible.
const approvalLockedKeys = computed(() => new Set(props.mode === 'edit' ? props.record?.approvalLock?.fieldKeys ?? [] : []));
const detailRowsLocked = computed(() => props.mode === 'edit' && !!props.record?.approvalLock?.detailRowsLocked);
const fieldReadonly = (f: SchemaField) => !!lockOf(f) || approvalLockedKeys.value.has(f.key) || !isWritable(f, ruleState.value) || lockedKeys.value.has(f.key) || (workflowLocked.value && f.key !== statusKey.value);
/** Names of the closed apps whose fields are shown in this block. */
const blockLocks = (b: SchemaBlock) => [...new Set(b.fields.filter((f) => !hiddenField(f)).map(lockOf)
  .filter((l) => l !== null).map((l) => resolveI18n(l.appName, locale.value)))];
const visibleBlocks = computed(() => props.schema.blocks.filter((b) => b.fields.some((f) => !hiddenField(f))));
const readOnlyForm = computed(() => ruleState.value.readOnlyAll);
const canSave = computed(() => !readOnlyForm.value && (!workflowLocked.value || canAdvance(workflow.value, storedStatus.value)));
// Errors nobody can see inline (record-level, or keyed to a field that is not rendered) go to the top banner.
const bannerLines = computed(() => {
  const shown = new Set(visibleBlocks.value.flatMap((b) => b.fields.filter((f) => !hiddenField(f)).map((f) => f.key)));
  const byKey = new Map(fields.value.map((f) => [f.key, f]));
  return Object.entries(errors.value).flatMap(([key, msg]) => {
    if (!msg) return [];
    if (key === '_record') return [msg];
    const f = byKey.get(key);
    return f && !shown.has(key) ? [`${resolveI18n(f.label, locale.value)}：${msg}`] : [];
  });
});

// undefined and null are the same "no value"; compare the form model, not zod's normalized output (R23h).
const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

const submitValid = handleSubmit((parsed) => {
  if (readOnlyForm.value) return; // Enter in an input can still submit a form without a save button
  if (Object.values(clientRowErrors.value).some((m) => m.size)) return;
  const details: Record<string, DetailRowPayload[]> = {};
  const at: Record<string, string[]> = {};
  for (const [key, rows] of Object.entries(detailRows.value)) {
    const { items, uidAt: uids } = buildDetailPayload(rows);
    if (items.length) {
      details[key] = items;
      at[key] = uids;
    }
  }
  uidAt.value = at;
  if (props.mode === 'create') return emit('submit', parsed, details);
  const changed = fields.value.filter((f) => !same(values[f.key], snapshot[f.key]));
  emit('submit', Object.fromEntries(changed.map((f) => [f.key, parsed[f.key]])), details);
});
const onSubmit = (e?: Event) => {
  rowsTried.value = true;
  return submitValid(e);
};

watch(() => props.serverErrors, (errs) => {
  if (errs) setErrors(Object.fromEntries(Object.entries(errs).map(([k, v]) => [k, v.join('；')])));
});

// A reloaded record (e.g. after a 409) replaces the form's values and the change-detection baseline.
watch(() => props.record, () => {
  snapshot = initialValues();
  for (const f of fillFields.value) appliedRef[f.key] = snapshot[f.key] ?? null; // a reload is not a user pick
  resetForm({ values: snapshot });
  detailRows.value = initialRows();
  uidAt.value = {};
  serverRowErrors.value = {};
  rowsTried.value = false;
});
</script>

<template>
  <form class="q-gutter-md" @submit.prevent="onSubmit">
    <div v-if="readOnlyForm" class="text-warning" data-testid="record-readonly">{{ t('dynamic.recordReadOnly') }}</div>
    <div v-if="frozen" class="text-warning" data-testid="frozen-readonly">{{ t('ledger.frozen') }}</div>
    <div v-else-if="workflowLocked" class="text-warning" data-testid="workflow-readonly">{{ t('docflow.readOnlyState') }}</div>
    <div v-if="bannerLines.length" class="text-negative" data-testid="record-error">
      <div v-for="line in bannerLines" :key="line">{{ line }}</div>
    </div>
    <q-expansion-item
      v-for="block in visibleBlocks" :key="block.id" :default-opened="!block.collapsed"
      :label="resolveI18n(block.label, locale)" header-class="text-subtitle1" bordered expand-separator
    >
      <div class="q-pa-md">
        <div v-for="app in blockLocks(block)" :key="app" class="text-warning text-caption q-mb-sm" data-testid="app-closed-lock">
          {{ t('dynamic.appClosedLock', { app }) }}
        </div>
        <div class="row q-col-gutter-md">
          <template v-for="f in block.fields" :key="f.key">
            <div v-if="!hiddenField(f)" :class="fieldColClass(block.columns, isFullWidth(f))">
              <div v-if="approvalLockedKeys.has(f.key)" class="row items-center no-wrap text-caption text-muted q-mb-xs" :data-testid="`approval-lock-${f.key}`">
                <q-icon name="lock" size="xs" class="q-mr-xs" aria-hidden="true" />
                <span>{{ t('approval.lockedHint') }}</span>
              </div>
              <DynamicField
                :field="f" :readonly="fieldReadonly(f)"
                :required="(ruleState.required.has(f.id) || f.key === statusKey) && !fieldReadonly(f)" :allowed-options="allowedFor(f)"
                :reference="fillReferences[f.key] ?? (record?.references ?? references)?.[f.key]"
              />
              <div v-if="f.key === statusKey && approvalHint" class="text-caption text-muted q-mt-xs" data-testid="approval-use-submit-hint">{{ approvalHint }}</div>
            </div>
          </template>
        </div>
      </div>
    </q-expansion-item>
    <DetailGrid
      v-for="d in schema.details ?? []" :key="d.moduleKey" :detail="d" :model-value="detailRows[d.moduleKey] ?? []"
      :readonly="readOnlyForm || workflowLocked || detailRowsLocked" :errors="gridErrors(d.moduleKey)" :header-values="formValues" :header-fields="fields" @update:model-value="(rows) => (detailRows[d.moduleKey] = rows)"
      @edit-cell="(uid, key) => clearServerCell(d.moduleKey, uid, key)"
    />
    <q-btn v-if="canSave" type="submit" color="primary" :label="t('dynamic.save')" :loading="saving" />
  </form>
</template>
