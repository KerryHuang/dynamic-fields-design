<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import TextareaInput from 'src/components/framework/TextareaInput.vue';
import { isConditionGroup, isNestedCondition, operatorNeedsValue } from 'src/domain/dynamic/condition';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { operatorsFor as ruleOperatorsFor, REF_OPS, refKindOf } from 'src/domain/dynamic/rule-engine';
import type { ConditionNode, FieldCondition, FilterOperator, SchemaField } from 'src/domain/dynamic/types';
import { fieldTypeRegistry, operatorsFor } from '../field-type-registry';

const MAX_LIST = 50;
const TEXT_TYPES = ['text', 'email', 'phone', 'url', 'autonumber'];

interface Row { cond: FieldCondition; raw?: string }

/**
 * Single-level all/any editor. It emits the unpruned condition on every change (rows still being filled stay in the editor);
 * the owner prunes when it applies or saves. `allowFieldRef` is the rule context: it adds "compare with another field"
 * and the rule operator table (textarea may use isEmpty/isNotEmpty); list filters leave it off.
 */
const props = defineProps<{ fields: SchemaField[]; modelValue?: ConditionNode; allowFieldRef?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: ConditionNode | undefined] }>();
const { t, locale } = useI18n();

const fieldOf = (id: number) => props.fields.find((f) => f.id === id);
const complex = computed(() => isNestedCondition(props.modelValue));

const mode = ref<'all' | 'any'>('all');
const rows = ref<Row[]>([]);
// What this editor last emitted: the parent echoing it back must not rebuild the rows (that would drop typed raw text).
let emitted = '';

function load(n?: ConditionNode) {
  if (JSON.stringify(n ?? null) === emitted) return;
  if (!n) { emitted = ''; mode.value = 'all'; rows.value = []; return; }
  const children = !isConditionGroup(n) ? [n] : 'all' in n ? n.all : n.any;
  mode.value = isConditionGroup(n) && 'any' in n ? 'any' : 'all';
  rows.value = children.filter((c): c is FieldCondition => !isConditionGroup(c)).map((cond) => ({ cond: { ...cond } }));
}
watch(() => props.modelValue, load, { immediate: true });

function publish() {
  const value = { [mode.value]: rows.value.map((r) => r.cond) } as ConditionNode;
  emitted = JSON.stringify(value);
  emit('update:modelValue', value);
}

const opsOf = (f: SchemaField) => (props.allowFieldRef ? ruleOperatorsFor(f) : operatorsFor(f));
const fieldOptions = computed(() => props.fields.map((f) => ({ value: String(f.id), label: resolveI18n(f.label, locale.value) })));
const modeOptions = computed(() => [
  { value: 'all', label: t('dynamic.filter.matchAll') },
  { value: 'any', label: t('dynamic.filter.matchAny') },
]);
const opLabel = (f: SchemaField, op: FilterOperator) =>
  t(f.type === 'multienum' && (op === 'in' || op === 'notIn') ? `dynamic.op.multienum.${op}` : `dynamic.op.${op}`);
const operatorOptions = (f?: SchemaField) => (f ? opsOf(f).map((op) => ({ value: op, label: opLabel(f, op) })) : []);

function setField(row: Row, id: string | string[] | null) {
  const f = fieldOf(Number(id));
  row.cond = { field: f?.id ?? 0, op: f ? (opsOf(f)[0] ?? 'eq') : 'eq' };
  row.raw = undefined;
  publish();
}
function setOp(row: Row, op: FilterOperator) {
  if (operatorNeedsValue(op) !== operatorNeedsValue(row.cond.op) || (isRef(row.cond) && !REF_OPS.includes(op))) {
    row.cond.value = undefined;
    row.raw = undefined;
  }
  row.cond.op = op;
  publish();
}
function addRow() { rows.value.push({ cond: { field: 0, op: 'eq' } }); publish(); }
function removeRow(i: number) { rows.value.splice(i, 1); publish(); }
function setValue(row: Row, v: unknown) { row.cond.value = v ?? undefined; publish(); }

// ---- fieldRef (rules only) ----
const isRef = (c: FieldCondition) => typeof c.value === 'object' && c.value !== null && !Array.isArray(c.value) && 'fieldRef' in c.value;
const refId = (c: FieldCondition) => (isRef(c) ? String((c.value as { fieldRef: number }).fieldRef) : null);
const canRef = (f: SchemaField, op: FilterOperator) => !!props.allowFieldRef && REF_OPS.includes(op) && refKindOf(f) !== null;
const refModeOptions = computed(() => [
  { value: 'value', label: t('designer.rules.valueFixed') },
  { value: 'field', label: t('designer.rules.valueField') },
]);
/** Fields whose index values are comparable with `f` (same kind), excluding `f` itself. */
const refCandidates = (f: SchemaField) =>
  props.fields.filter((x) => x.id !== f.id && refKindOf(x) === refKindOf(f))
    .map((x) => ({ value: String(x.id), label: resolveI18n(x.label, locale.value) }));
function setRefMode(row: Row, m: string | string[] | null) {
  row.raw = undefined;
  // `fieldRef: 0` means field mode with nothing picked yet; pruneCondition drops it as incomplete.
  row.cond.value = m === 'field' ? { fieldRef: 0 } : undefined;
  publish();
}
function setRef(row: Row, id: string | string[] | null) {
  row.cond.value = { fieldRef: Number(id) || 0 };
  publish();
}

/** Value input descriptor with the current locale applied over the zh-TW labels from the registry. */
function inputOf(f: SchemaField, op: FilterOperator) {
  const d = fieldTypeRegistry[f.type].filterInput(f, op);
  if (!d) return undefined;
  const p: Record<string, unknown> = { ...d.props, label: resolveI18n(f.label, locale.value) };
  if (Array.isArray(p.options)) {
    p.options = (p.options as { value: string; label: string }[]).map((o) => {
      const opt = f.options.find((x) => x.value === o.value);
      return opt ? { ...o, label: resolveI18n(opt.label, locale.value) } : o;
    });
  }
  return { component: d.component, props: p };
}
const isTextList = (f: SchemaField, op: FilterOperator) => TEXT_TYPES.includes(f.type) && operatorNeedsValue(op) === 'list';

/** R7: comma/newline separated, trimmed, no empties, no duplicates, at most 50. */
const splitList = (raw: string): string[] => [...new Set(raw.split(/[,\n]/).map((s) => s.trim()).filter(Boolean))];
const parseList = (raw: string): string[] => splitList(raw).slice(0, MAX_LIST);
const truncated = (row: Row) => splitList(rawOf(row)).length > MAX_LIST;
function setRaw(row: Row, v: string | null) {
  row.raw = v ?? '';
  row.cond.value = parseList(row.raw);
  publish();
}
const rawOf = (row: Row) => row.raw ?? (Array.isArray(row.cond.value) ? row.cond.value.join(', ') : '');
const pairPart = (row: Row, i: number) => (Array.isArray(row.cond.value) ? row.cond.value[i] : undefined) ?? null;
function setPair(row: Row, i: number, v: unknown) {
  const cur = Array.isArray(row.cond.value) ? [...row.cond.value] : [null, null];
  cur[i] = v;
  row.cond.value = cur;
  publish();
}

// ---- JSON fallback for nested conditions ----
const json = ref('');
const jsonError = ref(false);
watch(() => props.modelValue, (n) => {
  if (isNestedCondition(n) && JSON.stringify(n ?? null) !== emitted) json.value = JSON.stringify(n, null, 2);
}, { immediate: true });
function setJson(v: string | null) {
  json.value = v ?? '';
  try {
    const parsed = JSON.parse(json.value) as ConditionNode;
    if (typeof parsed !== 'object' || parsed === null || !isConditionGroup(parsed)) throw new Error('not a group');
    jsonError.value = false;
    emitted = JSON.stringify(parsed);
    emit('update:modelValue', parsed);
  } catch {
    jsonError.value = true;
  }
}
</script>

<template>
  <div v-if="complex" class="column q-gutter-sm">
    <div class="text-muted" data-testid="complex-notice">{{ t('designer.rules.jsonHint') }}</div>
    <TextareaInput :model-value="json" :label="t('designer.rules.conditionJson')" data-testid="condition-json" @update:model-value="setJson" />
    <div v-if="jsonError" class="text-negative text-caption" data-testid="condition-json-error">{{ t('designer.rules.jsonInvalid') }}</div>
  </div>
  <div v-else class="column q-gutter-sm condition-editor">
    <div class="row items-start no-wrap q-gutter-x-sm">
      <GenericSelect
        :model-value="mode" :label="t('dynamic.filter.title')" :options="modeOptions" required style="width: 12rem"
        @update:model-value="(v) => { mode = v === 'any' ? 'any' : 'all'; publish(); }"
      />
      <q-btn flat icon="add" no-caps :label="t('dynamic.filter.addCondition')" @click="addRow" />
    </div>
    <div v-for="(row, i) in rows" :key="i" class="row q-gutter-sm items-start no-wrap">
      <GenericSelect
        :model-value="row.cond.field ? String(row.cond.field) : null" :label="t('dynamic.filter.field')"
        :options="fieldOptions" class="col" @update:model-value="(v) => setField(row, v)"
      />
      <template v-if="fieldOf(row.cond.field)">
        <GenericSelect
          :model-value="row.cond.op" :label="t('dynamic.filter.operator')" :options="operatorOptions(fieldOf(row.cond.field))"
          required class="col" @update:model-value="(v) => setOp(row, v as FilterOperator)"
        />
        <template v-for="f in [fieldOf(row.cond.field)!]" :key="f.id">
          <GenericSelect
            v-if="canRef(f, row.cond.op)" :model-value="isRef(row.cond) ? 'field' : 'value'" :label="t('designer.rules.valueSource')"
            :options="refModeOptions" required class="col" @update:model-value="(v) => setRefMode(row, v)"
          />
          <GenericSelect
            v-if="canRef(f, row.cond.op) && isRef(row.cond)" :model-value="refId(row.cond) === '0' ? null : refId(row.cond)"
            :label="t('designer.rules.refField')" :options="refCandidates(f)" class="col" @update:model-value="(v) => setRef(row, v)"
          />
          <template v-else-if="inputOf(f, row.cond.op)">
            <div v-if="isTextList(f, row.cond.op)" class="col">
              <TextareaInput
                :model-value="rawOf(row)" :label="t('dynamic.filter.listHint')"
                data-testid="value-list" @update:model-value="(v) => setRaw(row, v)"
              />
              <div v-if="truncated(row)" class="text-warning text-caption" data-testid="list-truncated">
                {{ t('dynamic.filter.truncated') }}
              </div>
            </div>
            <template v-else-if="operatorNeedsValue(row.cond.op) === 'pair'">
              <component
                :is="inputOf(f, row.cond.op)!.component" v-bind="inputOf(f, row.cond.op)!.props" class="col"
                :model-value="pairPart(row, 0)" @update:model-value="(v: unknown) => setPair(row, 0, v)"
              />
              <component
                :is="inputOf(f, row.cond.op)!.component" v-bind="inputOf(f, row.cond.op)!.props" class="col"
                :model-value="pairPart(row, 1)" @update:model-value="(v: unknown) => setPair(row, 1, v)"
              />
            </template>
            <component
              :is="inputOf(f, row.cond.op)!.component" v-else v-bind="inputOf(f, row.cond.op)!.props" class="col"
              :model-value="row.cond.value ?? null" @update:model-value="(v: unknown) => setValue(row, v)"
            />
          </template>
        </template>
      </template>
      <q-btn flat round dense icon="close" :aria-label="t('dynamic.filter.removeCondition')" @click="removeRow(i)" />
    </div>
  </div>
</template>
