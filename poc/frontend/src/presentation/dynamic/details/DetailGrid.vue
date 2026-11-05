<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import InlineEditableDataTable from 'src/components/framework/InlineEditableDataTable.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { SchemaDetail, SchemaField } from 'src/domain/dynamic/types';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import { isWritable } from 'src/presentation/dynamic/rule-state';
import { newRowState, rowRuleState, type DetailRowState } from './detail-rows';

const props = defineProps<{
  detail: SchemaDetail;
  modelValue: DetailRowState[];
  readonly: boolean;
  /** Row uid -> field key (`_record` for the row) -> message. */
  errors: Map<string, Record<string, string>>;
  /** The header form's current values and fields, for parent-default columns of a new row. */
  headerValues?: Record<string, unknown>;
  headerFields?: SchemaField[];
}>();
const emit = defineEmits<{ 'update:modelValue': [rows: DetailRowState[]]; 'edit-cell': [uid: string, fieldKey: string] }>();
const { t, locale } = useI18n();

const fields = computed(() => props.detail.fields.filter((f) => f.key !== props.detail.parentFieldKey));
const columns = computed(() => fields.value.map((f) => ({ name: f.key, label: `${resolveI18n(f.label, locale.value)}${f.config.required ? ' *' : ''}` })));
const visibleRows = computed(() => props.modelValue.filter((r) => !r.deleted));
const states = computed(() => new Map(visibleRows.value.map((r) => [r.uid, rowRuleState(props.detail, r)])));
const stateOf = (r: DetailRowState) => states.value.get(r.uid)!;
const rowLocked = (r: DetailRowState) => props.readonly || stateOf(r).readOnlyAll;
const hiddenCell = (f: SchemaField, r: DetailRowState) => stateOf(r).hidden.has(f.id);

// Same binding as DynamicField, per row: the header row carries the labels.
function cellProps(f: SchemaField, r: DetailRowState) {
  const state = stateOf(r);
  const base = fieldTypeRegistry[f.type].props(f, locale.value);
  const options = base.options as { value: string }[] | undefined;
  const allowed = state.allowedOptions.get(f.id);
  return {
    ...base,
    label: '',
    // Per row (rules can require it); the column header marks only config.required.
    required: !props.readonly && state.required.has(f.id) && isWritable(f, state),
    readonly: props.readonly || !isWritable(f, state),
    ...(f.type === 'reference' && { reference: r.references?.[f.key] }),
    ...(options && allowed && { options: options.filter((o) => allowed.has(o.value)) }),
    error: props.errors.get(r.uid)?.[f.key],
  };
}

const labelOf = (key: string) => {
  const f = fields.value.find((x) => x.key === key);
  return f ? resolveI18n(f.label, locale.value) : key;
};
// Errors no cell shows: the row's own, and those on a rule-hidden cell.
function rowLines(r: DetailRowState): string[] {
  return Object.entries(props.errors.get(r.uid) ?? {}).flatMap(([key, msg]) => {
    if (key === '_record') return [msg];
    const f = fields.value.find((x) => x.key === key);
    return f && hiddenCell(f, r) ? [`${labelOf(key)}：${msg}`] : [];
  });
}
// Errors on rows the user deleted (e.g. a locked row) have no row to sit on.
const deletedLines = computed(() => props.modelValue.filter((r) => r.deleted).flatMap((r) =>
  Object.entries(props.errors.get(r.uid) ?? {}).map(([key, msg]) => (key === '_record' ? msg : `${labelOf(key)}：${msg}`))));

function setCell(r: DetailRowState, key: string, value: unknown) {
  emit('update:modelValue', props.modelValue.map((x) => (x.uid === r.uid ? { ...x, values: { ...x.values, [key]: value } } : x)));
  emit('edit-cell', r.uid, key);
}
const addRow = () => emit('update:modelValue', [...props.modelValue, newRowState(props.detail, props.headerValues, props.headerFields)]);
const removeRow = (r: DetailRowState) => emit('update:modelValue', r.id === undefined
  ? props.modelValue.filter((x) => x.uid !== r.uid)
  : props.modelValue.map((x) => (x.uid === r.uid ? { ...x, deleted: true } : x)));
</script>

<template>
  <div>
    <div class="text-subtitle1 q-mb-sm">{{ resolveI18n(detail.name, locale) }}</div>
    <div v-if="deletedLines.length" class="text-negative q-mb-sm" data-testid="detail-error">
      <div v-for="line in deletedLines" :key="line">{{ line }}</div>
    </div>
    <InlineEditableDataTable :columns="columns" :rows="visibleRows" :row-key="(r) => r.uid" :readonly="readonly" @add="addRow">
      <template v-for="f in fields" :key="f.key" #[`cell-${f.key}`]="{ row }">
        <div v-if="hiddenCell(f, row)" data-testid="cell-hidden" />
        <component
          :is="fieldTypeRegistry[f.type].component" v-else v-bind="cellProps(f, row)" :model-value="row.values[f.key]"
          @update:model-value="(v: unknown) => setCell(row, f.key, v)"
        />
      </template>
      <template #row-actions="{ row }">
        <q-btn
          v-if="!rowLocked(row)" flat dense round icon="delete" :aria-label="t('dynamic.details.deleteRow')"
          data-testid="delete-row" @click="removeRow(row)"
        />
        <div v-if="rowLines(row).length" class="text-negative text-caption" data-testid="row-error">
          <div v-for="line in rowLines(row)" :key="line">{{ line }}</div>
        </div>
      </template>
    </InlineEditableDataTable>
  </div>
</template>
