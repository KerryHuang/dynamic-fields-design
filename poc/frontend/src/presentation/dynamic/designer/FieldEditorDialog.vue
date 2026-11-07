<script setup lang="ts">
import { useQuasar, type QSelect } from 'quasar';
import { computed, nextTick, reactive, ref, toRef, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDesignerMutation, useModuleDetail, useModules, useRollupSources } from 'src/application/dynamic/queries/modules-queries';
import { useEntities } from 'src/application/dynamic/queries/relation-queries';
import { completeMap } from 'src/domain/dynamic/fill';
import type { MapRow } from 'src/domain/dynamic/conversion';
import { pruneCondition } from 'src/domain/dynamic/condition';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { supportsFilter, supportsSort } from 'src/domain/dynamic/field-capabilities';
import type { ConditionNode, DesignerField, FieldConfig, FieldType, I18nText, SchemaField } from 'src/domain/dynamic/types';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import { isFullWidth } from 'src/presentation/dynamic/form-layout';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import TextareaInput from 'src/components/framework/TextareaInput.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { errorFor, notifyApiError } from './api-error';
import FieldMapEditor from './FieldMapEditor.vue';
import { copySource, copyTarget, fieldOptions } from './field-options';
import I18nTextInput from './I18nTextInput.vue';
import OptionEditor from './OptionEditor.vue';
import { rollupTainted, toSchemaFields } from './schema-fields';

const props = defineProps<{ modelValue: boolean; moduleKey: string; blockId: number; field?: DesignerField }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean]; created: [field: DesignerField] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const TYPES: FieldType[] = [
  'text', 'textarea', 'integer', 'decimal', 'currency', 'percent', 'date', 'datetime', 'time',
  'boolean', 'email', 'phone', 'url', 'enum', 'multienum', 'user', 'formula', 'autonumber', 'reference', 'rollup', 'attachment',
];
const typeOptions = computed(() => TYPES.map((v) => ({ value: v, label: t(`designer.types.${v}`) })));

const form = reactive({
  type: 'text' as FieldType,
  label: {} as I18nText,
  required: false,
  readOnly: false,
  showInList: false,
  fullWidth: false,
  isFilterable: false,
  isSortable: false,
  maxLength: null as number | string | null,
  scale: null as number | string | null,
  formula: '',
  resultType: 'number' as NonNullable<FieldConfig['resultType']>,
  format: '',
  defaultValue: null as unknown,
  defaultToday: false,
  parentDefault: null as number | null,
  refKind: 'module' as 'module' | 'entity',
  refKey: null as string | null,
  onDelete: 'restrict' as NonNullable<FieldConfig['onDelete']>,
  relation: 'lookup' as NonNullable<FieldConfig['relation']>,
  scopeParent: false,
  rollupSource: null as number | null,
  rollupFunction: 'sum' as NonNullable<FieldConfig['rollup']>['function'],
  rollupAggregate: null as number | null,
  rollupCondition: undefined as ConditionNode | undefined,
  fillMode: 'default' as NonNullable<FieldConfig['fill']>['mode'],
  fillRows: [] as MapRow[],
});
const error = ref<ApiError | null>(null);
const labelInput = ref<{ validate(): Promise<boolean> } | null>(null);

const isEdit = computed(() => !!props.field);
const canFilter = computed(() => supportsFilter(form.type));
const isReference = computed(() => form.type === 'reference');
const canSort = computed(() => supportsSort(form.type) && !isReference.value);
const hasOptions = computed(() => form.type === 'enum' || form.type === 'multienum');
const hasMaxLength = computed(() => form.type === 'text' || form.type === 'textarea');
const isFormula = computed(() => form.type === 'formula');
const isRollup = computed(() => form.type === 'rollup');
const isComputed = computed(() => isFormula.value || isRollup.value || form.type === 'autonumber');
const isDetailRef = computed(() => isReference.value && form.refKind === 'module' && form.relation === 'detail');
const noDefault = computed(() => isComputed.value || isReference.value);
const hasScale = computed(() =>
  ['decimal', 'currency', 'percent'].includes(form.type) || (isFormula.value && form.resultType !== 'date'));
const defaultDisabled = computed(() => hasOptions.value && !isEdit.value);

watch(() => [props.modelValue, props.field?.id] as const, ([open]) => {
  if (!open) return;
  const f = props.field;
  error.value = null;
  Object.assign(form, {
    type: f?.type ?? 'text',
    label: { ...(f?.label ?? {}) },
    required: f?.config.required ?? false,
    readOnly: f?.config.readOnly ?? false,
    showInList: f?.config.showInList ?? false,
    fullWidth: f ? isFullWidth(f) : false,
    isFilterable: f?.config.isFilterable ?? false,
    isSortable: f?.config.isSortable ?? false,
    maxLength: f?.config.maxLength ?? null,
    scale: f?.config.scale ?? null,
    formula: f?.config.formula ?? '',
    resultType: f?.config.resultType ?? 'number',
    format: f?.config.format ?? '',
    defaultValue: f?.config.defaultValue ?? null,
    defaultToday: f?.config.defaultToday ?? false,
    parentDefault: f?.config.parentDefaultFieldId ?? null,
    refKind: f?.config.refTarget?.kind ?? 'module',
    refKey: f?.config.refTarget?.key ?? null,
    onDelete: f?.config.onDelete ?? 'restrict',
    relation: f?.config.relation ?? 'lookup',
    scopeParent: f?.config.scope === 'parent',
    rollupSource: f?.config.rollup?.sourceFieldId ?? null,
    rollupFunction: f?.config.rollup?.function ?? 'sum',
    rollupAggregate: f?.config.rollup?.aggregateFieldId ?? null,
    rollupCondition: f?.config.rollup?.condition ? JSON.parse(JSON.stringify(f.config.rollup.condition)) : undefined,
    fillMode: f?.config.fill?.mode ?? 'default',
    fillRows: (f?.config.fill?.map ?? []).map((m) => ({ ...m })),
  });
}, { immediate: true });
// A different type invalidates the type-specific settings.
watch(() => form.type, () => {
  if (isEdit.value) return;
  // 新建多行文字預設佔滿整列
  Object.assign(form, { fullWidth: form.type === 'textarea', isFilterable: false, isSortable: false, maxLength: null, scale: null, formula: '', resultType: 'number', format: '', defaultValue: null, defaultToday: false, parentDefault: null, refKind: 'module', refKey: null, onDelete: 'restrict',
    relation: 'lookup', scopeParent: false, rollupSource: null, rollupFunction: 'sum', rollupAggregate: null, rollupCondition: undefined, fillMode: 'default', fillRows: [] });
});
watch(() => form.refKind, () => { if (!isEdit.value) { form.refKey = null; form.relation = 'lookup'; } });
watch(() => form.refKey, () => { if (!isEdit.value) form.fillRows = []; });
watch(() => form.rollupFunction, (fn) => { if (fn === 'count') form.rollupAggregate = null; });
// The aggregate field and the condition are written against the source module's fields; only a user change clears them
// (a watcher would also fire when the dialog is reopened for another field).
function setRollupSource(id: number | null) {
  form.rollupSource = id;
  form.rollupAggregate = null;
  form.rollupCondition = undefined;
}

/** The registry renders the default-value input from a SchemaField, so describe the field being edited as one. */
const defaultField = computed<SchemaField>(() => ({
  id: props.field?.id ?? 0, key: 'defaultValue', type: form.type, label: { 'zh-TW': t('designer.defaultValue') },
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, scale: num(form.scale) },
  options: (props.field?.options ?? []).map((o) => ({ value: o.value, label: o.label, color: o.color, active: o.active })),
}));
const entry = computed(() => fieldTypeRegistry[form.type]);

/** QInput emits '' when cleared (and `.number` keeps it); treat that and NaN as "not set". */
const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);

const RESULT_TYPES = ['number', 'currency', 'date'] as const;
const resultTypeOptions = computed(() => RESULT_TYPES.map((v) => ({ value: v, label: t(`designer.formula.resultTypes.${v}`) })));

// Fields a formula may reference: numbers, dates and formulas of those result types (datetime and text cannot).
const moduleDetail = useModuleDetail(toRef(props, 'moduleKey'));
const referable = computed(() => (moduleDetail.data.value?.blocks ?? [])
  .flatMap((b) => b.fields)
  .filter((f) => f.status === 'Active' && f.id !== props.field?.id
    && (['integer', 'decimal', 'currency', 'percent', 'date', 'formula', 'rollup'] as FieldType[]).includes(f.type))
  .map((f) => ({ value: f.id, label: resolveI18n(f.label, locale.value) })));

const isDetailModule = computed(() => !!moduleDetail.data.value?.detailOf);

// Parent default: active fields of the header module with the same type (the server limits which types qualify).
const PARENT_DEFAULT_TYPES: FieldType[] = [
  'text', 'textarea', 'integer', 'decimal', 'currency', 'percent', 'date', 'datetime', 'time', 'boolean', 'email', 'phone', 'url', 'user',
];
const headerDetail = useModuleDetail(computed(() => moduleDetail.data.value?.detailOf ?? ''));
const canParentDefault = computed(() => isDetailModule.value && PARENT_DEFAULT_TYPES.includes(form.type));
const parentOptions = computed(() => (headerDetail.data.value?.blocks ?? []).flatMap((b) => b.fields)
  .filter((f) => f.status === 'Active' && f.type === form.type)
  .map((f) => ({ value: f.id, label: resolveI18n(f.label, locale.value) })));
const hasParentDefault = computed(() => canParentDefault.value && form.parentDefault != null);
const hasDefaultValue = computed(() => {
  const dv = form.defaultValue;
  return dv != null && dv !== '' && !(Array.isArray(dv) && dv.length === 0);
});
const todayOn = computed(() => form.type === 'date' && form.defaultToday && !hasParentDefault.value);
// One default at a time (the server rejects combinations); the other controls are disabled while one is set.
const defaultValueLocked = computed(() => todayOn.value || hasParentDefault.value);

// Rollup: the source is a reference field pointing here; aggregate field and condition use the source module's fields.
const sourcesQuery = useRollupSources(computed(() => (isRollup.value ? props.moduleKey : '')));
const rollupSourceOptions = computed(() => (sourcesQuery.data.value ?? []).map((s) => ({
  value: s.fieldId, label: `${resolveI18n(s.moduleName, locale.value)}．${resolveI18n(s.fieldLabel, locale.value)}`,
})));
const sourceModuleKey = computed(() => sourcesQuery.data.value?.find((s) => s.fieldId === form.rollupSource)?.moduleKey ?? '');
const sourceDetail = useModuleDetail(sourceModuleKey);
const tainted = computed(() => (sourceDetail.data.value ? rollupTainted(sourceDetail.data.value) : new Set<number>()));
const sourceFields = computed(() => (sourceDetail.data.value ? toSchemaFields(sourceDetail.data.value) : [])
  .filter((f) => !tainted.value.has(f.id)));
const AGGREGATABLE: FieldType[] = ['integer', 'decimal', 'currency', 'percent', 'date', 'formula'];
const aggregateOptions = computed(() => sourceFields.value.filter((f) => AGGREGATABLE.includes(f.type))
  .map((f) => ({ value: f.id, label: resolveI18n(f.label, locale.value) })));
const RESULT_FUNCTIONS = ['sum', 'count', 'min', 'max'] as const;
const functionOptions = computed(() => RESULT_FUNCTIONS.map((v) => ({ value: v, label: t(`designer.rollup.functions.${v}`) })));

// Reference targets: standalone non-detail modules, or the registered existing entities.
const modulesQuery = useModules();
const entitiesQuery = useEntities();
const refKindOptions = computed(() => (['module', 'entity'] as const)
  .map((v) => ({ value: v, label: t(v === 'module' ? 'designer.reference.targetKindModule' : 'designer.reference.targetKindEntity') })));
const refTargetOptions = computed(() => form.refKind === 'module'
  ? (modulesQuery.data.value ?? []).filter((m) => m.kind !== 'extension' && !m.detailOf)
    .map((m) => ({ value: m.key, label: `${resolveI18n(m.name, locale.value)} (${m.key})` }))
  : (entitiesQuery.data.value ?? []).map((e) => ({ value: e.key, label: resolveI18n(e.name, locale.value) })));
const relationOptions = computed(() => (['lookup', 'detail'] as const)
  .map((v) => ({ value: v, label: t(v === 'lookup' ? 'designer.reference.relationLookup' : 'designer.reference.relationDetail') })));
const ON_DELETE = [['restrict', 'Restrict'], ['setNull', 'SetNull'], ['cascade', 'Cascade']] as const;
const onDeleteOptions = computed(() => ON_DELETE.map(([value, name]) => ({
  value, label: t(`designer.reference.onDelete${name}`), hint: t(`designer.reference.onDelete${name}Hint`),
})));
// Fill: copy values of the picked record (a module target) into this record; `from` lists the target's fields, `to` this module's.
const hasFill = computed(() => isReference.value && form.refKind === 'module' && !!form.refKey && !isDetailRef.value);
const fillTargetDetail = useModuleDetail(computed(() => (hasFill.value ? form.refKey! : '')));
const fillFromOptions = computed(() => fieldOptions(fillTargetDetail.data.value, locale.value, copySource));
const fillToOptions = computed(() => fieldOptions(moduleDetail.data.value, locale.value, (f) => copyTarget(f) && f.id !== props.field?.id));
const fillModeOptions = computed(() => (['lock', 'default'] as const)
  .map((v) => ({ value: v, label: t(v === 'lock' ? 'docflow.fill.modeLock' : 'docflow.fill.modeDefault') })));
const typeSel = useSelectFilter(typeOptions);
const parentSel = useSelectFilter(parentOptions);
const insertSel = useSelectFilter(referable);
const insertSelEl = ref<QSelect | null>(null);
const resultSel = useSelectFilter(resultTypeOptions);
const sourceSel = useSelectFilter(rollupSourceOptions);
const functionSel = useSelectFilter(functionOptions);
const aggregateSel = useSelectFilter(aggregateOptions);
const kindSel = useSelectFilter(refKindOptions);
const targetSel = useSelectFilter(refTargetOptions);
const relationSel = useSelectFilter(relationOptions);
const onDeleteSel = useSelectFilter(onDeleteOptions);

const formulaWrap = ref<HTMLElement | null>(null);
async function insertField(id: number | null) {
  if (id == null) return;
  insertSelEl.value?.updateInputValue('');
  const ta = formulaWrap.value?.querySelector('textarea');
  const text = `{${id}}`;
  const at = ta?.selectionStart ?? form.formula.length;
  const end = ta?.selectionEnd ?? at;
  form.formula = form.formula.slice(0, at) + text + form.formula.slice(end);
  await nextTick();
  const caret = at + text.length;
  ta?.focus();
  ta?.setSelectionRange(caret, caret);
}

/** Preview only (today, serial 1); the backend owns validation. */
const formatExample = computed(() => {
  const d = new Date();
  const yyyy = String(d.getFullYear());
  const MM = String(d.getMonth() + 1).padStart(2, '0');
  return form.format
    .replace(/\{yyyyMM\}/g, yyyy + MM).replace(/\{yyyy\}/g, yyyy).replace(/\{yy\}/g, yyyy.slice(2)).replace(/\{MM\}/g, MM)
    .replace(/\{(0+)\}/, (_m, zeros: string) => '1'.padStart(zeros.length, '0'));
});

function buildRollup(sourceFieldId: number): NonNullable<FieldConfig['rollup']> {
  const condition = pruneCondition(form.rollupCondition);
  return {
    sourceFieldId,
    function: form.rollupFunction,
    ...(form.rollupFunction !== 'count' && form.rollupAggregate != null ? { aggregateFieldId: form.rollupAggregate } : {}),
    ...(condition ? { condition } : {}),
  };
}

function buildConfig(): FieldConfig {
  const dv = form.defaultValue;
  return {
    required: isDetailRef.value || (!isComputed.value && form.required),
    readOnly: !isComputed.value && form.readOnly,
    showInList: form.showInList,
    fullWidth: form.fullWidth,
    isFilterable: canFilter.value && form.isFilterable,
    isSortable: canSort.value && form.isSortable,
    ...(hasMaxLength.value && num(form.maxLength) !== undefined ? { maxLength: num(form.maxLength) } : {}),
    ...(hasScale.value && num(form.scale) !== undefined ? { scale: num(form.scale) } : {}),
    ...(isFormula.value ? { formula: form.formula, resultType: form.resultType } : {}),
    ...(form.type === 'autonumber' ? { format: form.format } : {}),
    ...(form.type === 'autonumber' && isDetailModule.value && form.scopeParent ? { scope: 'parent' as const } : {}),
    ...(isRollup.value && form.rollupSource != null ? { rollup: buildRollup(form.rollupSource) } : {}),
    ...(isReference.value && form.refKey ? { refTarget: { kind: form.refKind, key: form.refKey } } : {}),
    ...(isReference.value && form.refKind === 'module' ? { onDelete: isDetailRef.value ? 'cascade' as const : form.onDelete } : {}),
    ...(isDetailRef.value ? { relation: 'detail' as const } : {}),
    ...(hasFill.value && completeMap(form.fillRows).length ? { fill: { mode: form.fillMode, map: completeMap(form.fillRows) } } : {}),
    ...(!noDefault.value && !defaultDisabled.value && !defaultValueLocked.value && hasDefaultValue.value ? { defaultValue: dv } : {}),
    ...(!noDefault.value && todayOn.value ? { defaultToday: true } : {}),
    ...(!noDefault.value && hasParentDefault.value ? { parentDefaultFieldId: form.parentDefault! } : {}),
  };
}

const SHOWN = ['Label', 'Config.MaxLength', 'Config.Scale', 'Config.DefaultValue', 'Config.DefaultToday', 'Config.ParentDefaultFieldId', 'Config.Formula', 'Config.ResultType', 'Config.Format', 'Config.RefTarget', 'Config.OnDelete', 'Config.Rollup', 'Config.Relation', 'Config.Scope', 'Config.Fill'];
const onError = (e: unknown) => {
  const err = e as ApiError;
  error.value = err;
  notifyApiError((o) => $q.notify(o), err, SHOWN, t('designer.saveFailed'));
};

// menu: a new detail field turns this module into a detail, which the sidebar no longer lists
const create = useDesignerMutation(
  () => dynamicApi.addField(props.moduleKey, { blockId: props.blockId, type: form.type, label: form.label, config: buildConfig() }),
  () => props.moduleKey,
  ['menu'],
);
const update = useDesignerMutation(
  () => dynamicApi.updateField(props.field!.id, {
    type: props.field!.type, label: form.label, config: buildConfig(), blockId: props.field!.blockId,
  }),
  () => props.moduleKey,
);

async function submit() {
  if (!(await labelInput.value?.validate())) return;
  error.value = null;
  if (isEdit.value) {
    update.mutate(undefined, {
      onSuccess: () => {
        emit('update:modelValue', false);
      },
      onError,
    });
  }
  else create.mutate(undefined, { onSuccess: (f) => emit('created', f), onError });
}
const saving = computed(() => create.isPending.value || update.isPending.value);
</script>

<template>
  <q-dialog :model-value="modelValue" persistent @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="min-width: 560px; max-width: 90vw">
      <q-card-section class="text-h6">{{ isEdit ? t('designer.editField') : t('designer.addField') }}</q-card-section>
      <q-card-section class="q-gutter-md scroll" style="max-height: 70vh">
        <I18nTextInput ref="labelInput" v-model="form.label" :label="t('designer.fieldLabel')" :error="errorFor(error, 'Label')" />

        <div data-testid="type-select">
          <q-select v-model="form.type" :options="typeSel.filtered.value" :label="t('designer.fieldType')" emit-value map-options
            :readonly="isEdit" outlined dense use-input input-debounce="0" fill-input hide-selected @filter="typeSel.onFilter" @popup-hide="typeSel.reset">
            <template #no-option><SelectNoOption /></template>
          </q-select>
          <div v-if="isEdit" class="text-caption text-muted q-mt-xs">{{ t('designer.typeLocked') }}</div>
        </div>

        <div class="row q-gutter-md">
          <q-toggle
            v-if="!isComputed" :model-value="isDetailRef || form.required" data-testid="required-toggle" :disable="isDetailRef"
            :label="t('designer.required')" @update:model-value="(v) => form.required = v"
          />
          <q-toggle v-if="!isComputed" v-model="form.readOnly" data-testid="readonly-toggle" :label="t('designer.readOnly')" />
          <q-toggle v-model="form.showInList" :label="t('designer.showInList')" />
          <q-toggle v-model="form.fullWidth" data-testid="fullwidth-toggle" :label="t('designer.fullWidth')" />
        </div>
        <div class="row q-gutter-md">
          <div>
            <q-toggle v-model="form.isFilterable" data-testid="filterable-toggle" :disable="!canFilter" :label="t('designer.filterable')" />
            <div v-if="!canFilter" class="text-caption text-muted">{{ t('designer.filterUnsupported') }}</div>
          </div>
          <div>
            <q-toggle v-if="!isReference" v-model="form.isSortable" data-testid="sortable-toggle" :disable="!canSort" :label="t('designer.sortable')" />
            <div v-if="!canSort && !isReference" class="text-caption text-muted">{{ t('designer.sortUnsupported') }}</div>
          </div>
        </div>

        <div v-if="isFormula" class="q-gutter-sm">
          <div ref="formulaWrap" data-testid="formula-input">
            <TextareaInput
              v-model="form.formula" :label="t('designer.formula.expression')" :error="errorFor(error, 'Config.Formula')"
            />
            <div class="text-caption text-muted q-mt-xs">{{ t('designer.formula.hint') }}</div>
          </div>
          <q-select data-testid="insert-field" :model-value="null" :options="insertSel.filtered.value" :label="t('designer.formula.insertField')"
            emit-value map-options outlined dense @update:model-value="insertField" use-input input-debounce="0" @filter="insertSel.onFilter" @popup-hide="insertSel.reset" ref="insertSelEl">
            <template #no-option><SelectNoOption /></template>
          </q-select>
          <div data-testid="result-type">
            <q-select v-model="form.resultType" :options="resultSel.filtered.value" :label="t('designer.formula.resultType')"
              emit-value map-options outlined dense
              :error="!!errorFor(error, 'Config.ResultType')" :error-message="errorFor(error, 'Config.ResultType')" use-input input-debounce="0" fill-input hide-selected @filter="resultSel.onFilter" @popup-hide="resultSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
        </div>
        <div v-if="form.type === 'autonumber'" data-testid="autonumber-format">
          <q-input
            v-model="form.format" :label="t('designer.autonumber.format')" outlined dense
            :error="!!errorFor(error, 'Config.Format')" :error-message="errorFor(error, 'Config.Format')"
          />
          <div class="text-caption q-mt-xs" data-testid="autonumber-example">
            {{ t('designer.autonumber.example') }}：{{ formatExample }}
          </div>
          <div class="text-caption text-muted">{{ t('designer.autonumber.hint') }}</div>
          <div v-if="isDetailModule" data-testid="autonumber-scope">
            <q-toggle v-model="form.scopeParent" :label="t('designer.autonumber.scopeParent')" :disable="isEdit" />
            <div class="text-caption text-muted">{{ t('designer.autonumber.scopeParentHint') }}</div>
            <div v-if="errorFor(error, 'Config.Scope')" class="text-caption text-negative">{{ errorFor(error, 'Config.Scope') }}</div>
          </div>
        </div>

        <div v-if="isRollup" class="q-gutter-sm" data-testid="rollup-settings">
          <div data-testid="rollup-source">
            <q-select :model-value="form.rollupSource" :options="sourceSel.filtered.value" :label="t('designer.rollup.source')"
              :hint="rollupSourceOptions.length ? t('designer.rollup.sourceHint') : t('designer.rollup.noSources')"
              emit-value map-options outlined dense @update:model-value="setRollupSource" use-input input-debounce="0" fill-input hide-selected @filter="sourceSel.onFilter" @popup-hide="sourceSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div data-testid="rollup-function">
            <q-select v-model="form.rollupFunction" :options="functionSel.filtered.value" :label="t('designer.rollup.function')" emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected @filter="functionSel.onFilter" @popup-hide="functionSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div v-if="form.rollupFunction !== 'count'" data-testid="rollup-aggregate">
            <q-select v-model="form.rollupAggregate" :options="aggregateSel.filtered.value" :label="t('designer.rollup.aggregate')"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected @filter="aggregateSel.onFilter" @popup-hide="aggregateSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div v-if="form.rollupSource != null" data-testid="rollup-condition">
            <div class="text-caption text-muted">{{ t('designer.rollup.condition') }}</div>
            <ConditionEditor v-model="form.rollupCondition" :fields="sourceFields" />
          </div>
          <div class="text-caption text-muted">{{ t('designer.rollup.hint') }}</div>
          <div v-if="errorFor(error, 'Config.Rollup')" class="text-caption text-negative">{{ errorFor(error, 'Config.Rollup') }}</div>
        </div>

        <div v-if="isReference" class="q-gutter-sm">
          <div data-testid="ref-kind">
            <q-select v-model="form.refKind" :options="kindSel.filtered.value" :label="t('designer.reference.targetKind')"
              emit-value map-options outlined dense :readonly="isEdit" use-input input-debounce="0" fill-input hide-selected @filter="kindSel.onFilter" @popup-hide="kindSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div data-testid="ref-target">
            <q-select v-model="form.refKey" :options="targetSel.filtered.value" :label="t('designer.reference.target')"
              emit-value map-options outlined dense :readonly="isEdit"
              :error="!!errorFor(error, 'Config.RefTarget')" :error-message="errorFor(error, 'Config.RefTarget')" use-input input-debounce="0" fill-input hide-selected @filter="targetSel.onFilter" @popup-hide="targetSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div v-if="form.refKind === 'module'" data-testid="ref-relation">
            <q-select v-model="form.relation" :options="relationSel.filtered.value" :label="t('designer.reference.relation')"
              emit-value map-options outlined dense :readonly="isEdit"
              :error="!!errorFor(error, 'Config.Relation')" :error-message="errorFor(error, 'Config.Relation')"
              :hint="form.relation === 'detail' ? t('designer.reference.relationDetailHint') : undefined" use-input input-debounce="0" fill-input hide-selected @filter="relationSel.onFilter" @popup-hide="relationSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div v-if="form.refKind === 'module' && !isDetailRef" data-testid="ref-ondelete">
            <q-select v-model="form.onDelete" :options="onDeleteSel.filtered.value" :label="t('designer.reference.onDelete')"
              emit-value map-options outlined dense
              :error="!!errorFor(error, 'Config.OnDelete')" :error-message="errorFor(error, 'Config.OnDelete')" use-input input-debounce="0" fill-input hide-selected @filter="onDeleteSel.onFilter" @popup-hide="onDeleteSel.reset">
              <template #no-option><SelectNoOption /></template>
              <template #option="{ opt, itemProps }">
                <q-item v-bind="itemProps">
                  <q-item-section>
                    <q-item-label>{{ opt.label }}</q-item-label>
                    <q-item-label caption>{{ opt.hint }}</q-item-label>
                  </q-item-section>
                </q-item>
              </template>
            </q-select>
          </div>
        </div>

        <div v-if="hasFill" class="q-gutter-sm" data-testid="fill-settings">
          <div class="text-subtitle2">{{ t('docflow.fill.title') }}</div>
          <div class="text-caption text-muted">{{ t('docflow.fill.hint') }}</div>
          <q-btn-toggle
            v-model="form.fillMode" data-testid="fill-mode" no-caps unelevated toggle-color="primary" :options="fillModeOptions"
          />
          <div class="text-caption text-muted">{{ t(form.fillMode === 'lock' ? 'docflow.fill.modeLockHint' : 'docflow.fill.modeDefaultHint') }}</div>
          <FieldMapEditor
            v-model="form.fillRows" :from-options="fillFromOptions" :to-options="fillToOptions"
            :from-label="t('docflow.fill.from')" :to-label="t('docflow.fill.to')" :error="errorFor(error, 'Config.Fill')"
          />
        </div>

        <div v-if="hasMaxLength" data-testid="maxlength-input">
          <q-input
            v-model.number="form.maxLength" type="number" :label="t('designer.maxLength')" outlined dense
            :error="!!errorFor(error, 'Config.MaxLength')" :error-message="errorFor(error, 'Config.MaxLength')"
          />
        </div>
        <div v-if="hasScale" data-testid="scale-input">
          <q-input
            v-model.number="form.scale" type="number" min="0" max="6" :label="t('designer.scale')" outlined dense
            :error="!!errorFor(error, 'Config.Scale')" :error-message="errorFor(error, 'Config.Scale')"
          />
        </div>

        <div v-if="!noDefault" data-testid="default-value">
          <div v-if="defaultDisabled" class="text-caption text-muted">
            {{ t('designer.defaultValue') }}：{{ t('designer.defaultAfterOptions') }}
          </div>
          <div v-else class="row items-start no-wrap q-gutter-sm">
            <component
              :is="entry.component" v-bind="entry.props(defaultField, locale)" v-model="form.defaultValue"
              class="col" :error="errorFor(error, 'Config.DefaultValue')" :readonly="defaultValueLocked"
            />
            <q-btn flat dense icon="backspace" :title="t('designer.clearDefault')" :disable="defaultValueLocked" @click="form.defaultValue = null" />
          </div>
          <div v-if="form.type === 'date' && !defaultDisabled" data-testid="default-today">
            <q-toggle v-model="form.defaultToday" :disable="hasParentDefault || hasDefaultValue" :label="t('designer.defaultToday')" />
            <div v-if="errorFor(error, 'Config.DefaultToday')" class="text-caption text-negative">{{ errorFor(error, 'Config.DefaultToday') }}</div>
          </div>
          <div v-if="canParentDefault" data-testid="parent-default">
            <q-select v-model="form.parentDefault" :options="parentSel.filtered.value" :label="t('designer.parentDefault')"
              :hint="t('designer.parentDefaultHint')" emit-value map-options outlined dense clearable
              :disable="todayOn || hasDefaultValue"
              :error="!!errorFor(error, 'Config.ParentDefaultFieldId')" :error-message="errorFor(error, 'Config.ParentDefaultFieldId')"
              use-input input-debounce="0" fill-input hide-selected @filter="parentSel.onFilter" @popup-hide="parentSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
        </div>

        <OptionEditor v-if="hasOptions && field" :module-key="moduleKey" :field-id="field.id" :options="field.options" />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('designer.close')" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" :label="t('designer.save')" :loading="saving" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
