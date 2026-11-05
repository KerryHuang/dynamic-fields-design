<script setup lang="ts">
import { toTypedSchema } from '@vee-validate/zod';
import { useForm } from 'vee-validate';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DetailRowPayload, DynamicRecord, ModuleSchema, ReferenceInfo, SchemaField } from 'src/domain/dynamic/types';
import { buildZodSchema } from 'src/presentation/dynamic/build-zod-schema';
import DetailGrid from 'src/presentation/dynamic/details/DetailGrid.vue';
import { fieldColClass, isFullWidth } from 'src/presentation/dynamic/form-layout';
import { buildDetailPayload, rowErrors, toRowStates, validateRows, type DetailRowState } from 'src/presentation/dynamic/details/detail-rows';
import { defaultValueOf } from 'src/presentation/dynamic/field-type-registry';
import { isWritable, ruleStateOf } from 'src/presentation/dynamic/rule-state';
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
const emit = defineEmits<{ submit: [values: Record<string, unknown>, details: Record<string, DetailRowPayload[]>] }>();
const { t, locale } = useI18n();

const fields = computed(() => props.schema.blocks.flatMap((b) => b.fields));

function initialValues(): Record<string, unknown> {
  return Object.fromEntries(fields.value.map((f) => [
    f.key,
    (props.mode === 'edit' ? props.record?.values[f.key] : (props.initial?.[f.key] ?? defaultValueOf(f))) ?? null,
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
  d.moduleKey, rowsTried.value ? validateRows(d, detailRows.value[d.moduleKey] ?? [], locale.value) : new Map<string, Record<string, string>>(),
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

const { handleSubmit, values, errors, setErrors, resetForm } = useForm({
  // Identity `cast`: vee-validate would otherwise rewrite the model with zod-normalized values (e.g. trimmed text).
  validationSchema: computed(() => ({
    ...toTypedSchema(buildZodSchema(props.schema, props.mode, props.record, ruleState.value, {
      storedRuleState: storedRuleState.value, locale: locale.value,
    })),
    cast: (v: unknown) => v,
  })),
  initialValues: snapshot,
  // Rule-hidden fields unmount but keep their value; a changed one is still submitted (decision 8).
  keepValuesOnUnmount: true,
});
watch(values, (v) => { formValues.value = v as Record<string, unknown>; }, { deep: true, immediate: true });

const hiddenField = (f: SchemaField) => ruleState.value.hidden.has(f.id);
const visibleBlocks = computed(() => props.schema.blocks.filter((b) => b.fields.some((f) => !hiddenField(f))));
const readOnlyForm = computed(() => ruleState.value.readOnlyAll);
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
    <div v-if="bannerLines.length" class="text-negative" data-testid="record-error">
      <div v-for="line in bannerLines" :key="line">{{ line }}</div>
    </div>
    <q-expansion-item
      v-for="block in visibleBlocks" :key="block.id" :default-opened="!block.collapsed"
      :label="resolveI18n(block.label, locale)" header-class="text-subtitle1" bordered expand-separator
    >
      <div class="q-pa-md">
        <div class="row q-col-gutter-md">
          <template v-for="f in block.fields" :key="f.key">
            <div v-if="!hiddenField(f)" :class="fieldColClass(block.columns, isFullWidth(f))">
              <DynamicField
                :field="f" :readonly="!isWritable(f, ruleState)"
                :required="ruleState.required.has(f.id) && isWritable(f, ruleState)" :allowed-options="ruleState.allowedOptions.get(f.id)"
                :reference="(record?.references ?? references)?.[f.key]"
              />
            </div>
          </template>
        </div>
      </div>
    </q-expansion-item>
    <DetailGrid
      v-for="d in schema.details ?? []" :key="d.moduleKey" :detail="d" :model-value="detailRows[d.moduleKey] ?? []"
      :readonly="readOnlyForm" :errors="gridErrors(d.moduleKey)" :header-values="formValues" :header-fields="fields" @update:model-value="(rows) => (detailRows[d.moduleKey] = rows)"
      @edit-cell="(uid, key) => clearServerCell(d.moduleKey, uid, key)"
    />
    <q-btn v-if="!readOnlyForm" type="submit" color="primary" :label="t('dynamic.save')" :loading="saving" />
  </form>
</template>
