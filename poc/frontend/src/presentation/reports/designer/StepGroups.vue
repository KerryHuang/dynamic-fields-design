<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ModuleCatalog, ReportFieldRef } from 'src/domain/dynamic/report-types';
import { findEntry, listFields, refKey } from '../report-catalog';
import { addMeasure, hasDedupeNote, type DesignerState, type Unavailable } from './designer-state';

const props = defineProps<{ state: DesignerState; catalog: ModuleCatalog; unavailable: Unavailable }>();
const { t } = useI18n();
const s = props.state;

const entries = computed(() => listFields(props.catalog, s.child, t('reports.titleField')));
const choice = (e: { ref: ReportFieldRef; label: string }) => ({ value: refKey(e.ref), label: e.label });
const groupable = computed(() => entries.value.filter((e) => e.field.canGroup).map(choice));
const addingGroup = ref<string | null>(null);
function addGroup(key: string | null) {
  const e = entries.value.find((x) => refKey(x.ref) === key);
  if (e) s.groups.push({ field: e.ref, bucket: e.field.isDate ? 'month' : null });
  addingGroup.value = null;
}
const BUCKETS = ['day', 'week', 'month', 'quarter', 'year'];

// 量值的欄位選項依後端旗標限制：加總與平均限 canSum，最小與最大限 canMinMax
const kind = ref('sum');
const measureField = ref<string | null>(null);
const needsField = (k: string) => ['sum', 'avg', 'min', 'max'].includes(k);
const kinds = computed(() => ['sum', 'avg', 'min', 'max', 'countMain', ...(s.child ? ['countChild'] : [])].map((k) => ({ value: k, label: t(`reports.designer.kind.${k}`) })));
const fieldChoices = computed(() => entries.value
  .filter((e) => (kind.value === 'sum' || kind.value === 'avg' ? e.field.canSum : e.field.canMinMax)).map(choice));
function pickKind(k: string) { kind.value = k; measureField.value = null; }
const canAddMeasure = computed(() => !needsField(kind.value) || !!measureField.value);
function add() {
  const e = entries.value.find((x) => refKey(x.ref) === measureField.value);
  addMeasure(s, kind.value, needsField(kind.value) ? (e?.ref ?? null) : null);
  measureField.value = null;
}
const measureText = (i: number) => {
  const m = s.measures[i]!;
  const e = findEntry(entries.value, m.field);
  return `${t(`reports.designer.kind.${m.kind}`, m.kind)}${m.field ? ` ${e ? e.label : `${t('reports.fieldNo', { id: m.field.fieldId })}${t('reports.unavailableMark')}`}` : ''}`;
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
          v-if="findEntry(entries, g.field)!.field.isDate" dense outlined emit-value map-options style="min-width: 8rem" :model-value="g.bucket ?? 'month'"
          :options="BUCKETS.map((b) => ({ value: b, label: t(`reports.designer.bucket.${b}`) }))" :data-testid="`group-bucket-${i}`"
          @update:model-value="g.bucket = $event"
        />
      </template>
      <span v-else class="text-negative" :data-testid="`group-unavailable-${i}`">{{ t('reports.fieldNo', { id: g.field.fieldId }) }}{{ t('reports.unavailableMark') }}</span>
      <q-btn flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`group-remove-${i}`" @click="s.groups.splice(i, 1)" />
    </div>
    <q-select
      dense outlined emit-value map-options :label="t('reports.designer.addGroup')" :options="groupable" :model-value="addingGroup"
      data-testid="group-add" @update:model-value="addGroup"
    />

    <q-separator />
    <div class="text-subtitle2">{{ t('reports.designer.measures') }}</div>
    <div v-for="(m, i) in s.measures" :key="m.id" class="row items-center q-gutter-x-sm" :data-testid="`measure-row-${i}`">
      <span :class="{ 'text-negative': unavailable.measures.includes(i) }">{{ measureText(i) }}</span>
      <q-btn flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`measure-remove-${i}`" @click="s.measures.splice(i, 1)" />
    </div>
    <div class="row q-gutter-x-sm items-start">
      <q-select
        dense outlined emit-value map-options class="col-3" :options="kinds" :model-value="kind" data-testid="measure-kind" @update:model-value="pickKind"
      />
      <q-select
        v-if="needsField(kind)" dense outlined emit-value map-options class="col" :label="t('reports.designer.measureField')" :options="fieldChoices"
        :model-value="measureField" data-testid="measure-field" @update:model-value="measureField = $event"
      />
      <q-btn outline no-caps :label="t('reports.designer.addMeasure')" :disable="!canAddMeasure" data-testid="measure-add" @click="add" />
    </div>
    <div v-if="hasDedupeNote(s)" class="text-caption text-warning" data-testid="dedupe-note">{{ t('reports.dedupedNote') }}</div>
  </div>
</template>
