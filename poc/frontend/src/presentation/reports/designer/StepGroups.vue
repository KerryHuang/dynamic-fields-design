<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ModuleCatalog, ReportFieldRef } from 'src/domain/dynamic/report-types';
import { findEntry, listFields, refKey, valueText } from '../report-catalog';
import { addMeasure, hasDedupeNote, operatorsOf, removeGroup, removeMeasure, type DesignerState, type Unavailable } from './designer-state';
import FilterRow from './FilterRow.vue';

const props = defineProps<{ state: DesignerState; catalog: ModuleCatalog; unavailable: Unavailable }>();
const { t } = useI18n();
const s = props.state;

const entries = computed(() => listFields(props.catalog, s.child, t('reports.titleField')));
const choice = (e: { ref: ReportFieldRef; label: string }) => ({ value: refKey(e.ref), label: e.label });
// 同一欄位只分組一次（日期欄位用兩種分桶會互相覆蓋，後端也不收）
const groupable = computed(() => entries.value
  .filter((e) => e.field.canGroup && !s.groups.some((g) => refKey(g.field) === refKey(e.ref))).map(choice));
const addingGroup = ref<string | null>(null);
function addGroup(key: string | null) {
  const e = entries.value.find((x) => refKey(x.ref) === key);
  if (e) s.groups.push({ field: e.ref, bucket: e.field.isDate ? 'month' : null });
  addingGroup.value = null;
}
const BUCKETS = ['day', 'week', 'month', 'quarter', 'year'];
const bucketOptions = computed(() => [{ value: '', label: t('reports.designer.bucket.none') }, ...BUCKETS.map((b) => ({ value: b, label: t(`reports.designer.bucket.${b}`) }))]);

// 量值的欄位選項依後端旗標限制：加總與平均限 canSum，最小與最大限 canMinMax；條件占比選條件欄位
const kind = ref('sum');
const measureField = ref<string | null>(null);
const needsField = (k: string) => ['sum', 'avg', 'min', 'max'].includes(k);
const kinds = computed(() => ['sum', 'avg', 'min', 'max', 'countMain', ...(s.child ? ['countChild'] : []), 'ratio'].map((k) => ({ value: k, label: t(`reports.designer.kind.${k}`) })));
const fieldChoices = computed(() => entries.value
  .filter((e) => (kind.value === 'sum' || kind.value === 'avg' ? e.field.canSum : e.field.canMinMax)).map(choice));
const conditionChoices = computed(() => entries.value.filter((e) => operatorsOf(e.field).length > 0).map(choice));
function pickKind(k: string) { kind.value = k; measureField.value = null; }
const canAddMeasure = computed(() => (!needsField(kind.value) && kind.value !== 'ratio') || !!measureField.value);
function add() {
  const e = entries.value.find((x) => refKey(x.ref) === measureField.value);
  if (kind.value === 'ratio') {
    if (e) addMeasure(s, 'ratio', null, { field: e.ref, op: operatorsOf(e.field)[0]!, value: undefined });
  } else {
    addMeasure(s, kind.value, needsField(kind.value) ? (e?.ref ?? null) : null);
  }
  measureField.value = null;
}
const fieldText = (r: ReportFieldRef) => {
  const e = findEntry(entries.value, r);
  return e ? e.label : `${t('reports.fieldNo', { id: r.fieldId })}${t('reports.unavailableMark')}`;
};
const measureText = (i: number) => {
  const m = s.measures[i]!;
  const c = m.condition;
  const cond = c
    ? `${fieldText(c.field)} ${t(`dynamic.op.${c.op}`, c.op)} ${valueText(c.value, findEntry(entries.value, c.field)?.field, t('reports.inactiveMark'))}`.trim()
    : '';
  return [t(`reports.designer.kind.${m.kind}`, m.kind), m.field ? fieldText(m.field) : '', cond].filter(Boolean).join(' ');
};
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="designer-step-3">
    <div class="text-subtitle2">{{ t('reports.designer.groups') }}</div>
    <div class="text-caption text-grey-7">{{ t('reports.designer.groupsHint') }}</div>
    <div v-for="(g, i) in s.groups" :key="i" class="row items-center q-gutter-x-sm" :data-testid="`group-row-${i}`">
      <template v-if="findEntry(entries, g.field)">
        <span>{{ findEntry(entries, g.field)!.label }}</span>
        <q-select
          v-if="findEntry(entries, g.field)!.field.isDate" dense outlined emit-value map-options style="min-width: 8rem" :model-value="g.bucket ?? ''"
          :options="bucketOptions" :data-testid="`group-bucket-${i}`" @update:model-value="g.bucket = $event || null"
        />
      </template>
      <span v-else class="text-negative" :data-testid="`group-unavailable-${i}`">{{ t('reports.fieldNo', { id: g.field.fieldId }) }}{{ t('reports.unavailableMark') }}</span>
      <q-btn flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`group-remove-${i}`" @click="removeGroup(s, i)" />
    </div>
    <q-select
      dense outlined emit-value map-options :label="t('reports.designer.addGroup')" :options="groupable" :model-value="addingGroup"
      data-testid="group-add" @update:model-value="addGroup"
    />

    <q-separator />
    <div class="text-subtitle2">{{ t('reports.designer.measures') }}</div>
    <div v-for="(m, i) in s.measures" :key="m.id" class="column" :data-testid="`measure-row-${i}`">
      <div class="row items-center q-gutter-x-sm">
        <span :class="{ 'text-negative': unavailable.measures.includes(i) }" :data-testid="`measure-text-${i}`">{{ measureText(i) }}</span>
        <q-btn flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`measure-remove-${i}`" @click="removeMeasure(s, i)" />
      </div>
      <FilterRow v-if="m.kind === 'ratio' && m.condition" class="q-ml-md" :filter="m.condition" :entries="entries" :tid="`measure-cond-${i}`" :removable="false" />
    </div>
    <div class="row q-gutter-x-sm items-start">
      <q-select
        dense outlined emit-value map-options class="col-3" :options="kinds" :model-value="kind" data-testid="measure-kind" @update:model-value="pickKind"
      />
      <q-select
        v-if="needsField(kind)" dense outlined emit-value map-options class="col" :label="t('reports.designer.measureField')" :options="fieldChoices"
        :model-value="measureField" data-testid="measure-field" @update:model-value="measureField = $event"
      />
      <q-select
        v-else-if="kind === 'ratio'" dense outlined emit-value map-options class="col" :label="t('reports.designer.ratioField')" :options="conditionChoices"
        :model-value="measureField" data-testid="ratio-field" @update:model-value="measureField = $event"
      />
      <q-btn outline no-caps :label="t('reports.designer.addMeasure')" :disable="!canAddMeasure" data-testid="measure-add" @click="add" />
    </div>
    <div v-if="hasDedupeNote(s)" class="text-caption text-warning" data-testid="dedupe-note">{{ t('reports.dedupedNote') }}</div>

    <div v-if="unavailable.sort.length" data-testid="sort-unavailable">
      <div class="text-subtitle2 text-negative">{{ t('reports.designer.unavailableSort') }}</div>
      <div v-for="i in unavailable.sort" :key="i" class="row items-center">
        <span class="text-negative">{{ s.sort[i]!.key }}{{ t('reports.unavailableMark') }}</span>
        <q-btn flat dense no-caps color="primary" :label="t('reports.designer.remove')" :data-testid="`sort-remove-${i}`" @click="s.sort.splice(i, 1)" />
      </div>
    </div>
  </div>
</template>
