<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ModuleCatalog, ReportFieldRef } from 'src/domain/dynamic/report-types';
import type { FilterOperator } from 'src/domain/dynamic/types';
import { findEntry, listFields, refKey } from '../report-catalog';
import { operatorsOf, setPrimaryDate, type DesignerState, type Unavailable } from './designer-state';
import FilterValueInput from './FilterValueInput.vue';

const props = defineProps<{ state: DesignerState; catalog: ModuleCatalog; unavailable: Unavailable }>();
const { t } = useI18n();
const s = props.state;

const entries = computed(() => listFields(props.catalog, s.child, t('reports.titleField')));
const filterable = computed(() => entries.value.filter((e) => operatorsOf(e.field).length > 0));
const dateEntries = computed(() => entries.value.filter((e) => e.field.isDate));
const choice = (e: { ref: ReportFieldRef; label: string }) => ({ value: refKey(e.ref), label: e.label });
const adding = ref<string | null>(null);

function add(key: string | null) {
  const e = filterable.value.find((x) => refKey(x.ref) === key);
  if (e) s.filters.push({ field: e.ref, op: operatorsOf(e.field)[0]!, value: undefined });
  adding.value = null;
}
function setOp(i: number, op: string) {
  s.filters[i] = { field: s.filters[i]!.field, op, value: undefined };
}
function pickPrimary(key: string | null) {
  setPrimaryDate(s, dateEntries.value.find((e) => refKey(e.ref) === key)?.ref ?? null);
}
const RANGES = ['today', 'thisWeek', 'lastWeek', 'thisMonth', 'lastMonth', 'thisYear', 'lastYear', 'lastDays', 'lastWeeks', 'lastMonths'];
const rangeOptions = computed(() => [{ value: '', label: t('reports.designer.none') }, ...RANGES.map((k) => ({ value: k, label: t(`reports.designer.range.${k}`) }))]);
function pickRange(kind: string) {
  s.dateRange = kind ? { kind, n: kind.startsWith('last') ? (s.dateRange?.n ?? 30) : null } : null;
}
const rowEntry = (i: number) => findEntry(entries.value, s.filters[i]!.field);
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="designer-step-2">
    <div v-for="(f, i) in s.filters" :key="i" class="row items-start q-gutter-x-sm no-wrap" :data-testid="`filter-row-${i}`">
      <template v-if="rowEntry(i)">
        <div class="col-3 q-pt-sm">{{ rowEntry(i)!.label }}</div>
        <q-select
          dense outlined emit-value map-options class="col-2" :model-value="f.op" :data-testid="`filter-op-${i}`"
          :options="operatorsOf(rowEntry(i)!.field).map((o: FilterOperator) => ({ value: o, label: t(`dynamic.op.${o}`) }))"
          @update:model-value="setOp(i, $event)"
        />
        <FilterValueInput class="col" :field="rowEntry(i)!.field" :op="f.op" :model-value="f.value" @update:model-value="f.value = $event" />
      </template>
      <div v-else class="col text-negative" :data-testid="`filter-unavailable-${i}`">
        {{ t('reports.fieldNo', { id: f.field.fieldId }) }}{{ t('reports.unavailableMark') }}
      </div>
      <q-btn flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`filter-remove-${i}`" @click="s.filters.splice(i, 1)" />
    </div>
    <q-select
      dense outlined emit-value map-options :label="t('reports.designer.addCondition')" :options="filterable.map(choice)"
      :model-value="adding" data-testid="filter-add" @update:model-value="add"
    />

    <q-separator />
    <div class="text-subtitle2">{{ t('reports.designer.dateTitle') }}</div>
    <div v-if="unavailable.primaryDate" class="text-negative" data-testid="primary-unavailable">
      {{ t('reports.fieldNo', { id: s.primaryDateField!.fieldId }) }}{{ t('reports.unavailableMark') }}
      <q-btn flat dense no-caps color="primary" :label="t('reports.designer.remove')" data-testid="primary-remove" @click="setPrimaryDate(s, null)" />
    </div>
    <q-select
      v-else dense outlined emit-value map-options clearable :label="t('reports.designer.primaryDate')" :options="dateEntries.map(choice)"
      :model-value="s.primaryDateField ? refKey(s.primaryDateField) : null" data-testid="primary-date" @update:model-value="pickPrimary"
    />
    <div class="row q-gutter-x-sm">
      <q-select
        dense outlined emit-value map-options class="col" :label="t('reports.designer.dateRange')" :options="rangeOptions"
        :disable="!s.primaryDateField" :model-value="s.dateRange?.kind ?? ''" data-testid="date-range" @update:model-value="pickRange"
      />
      <q-input
        v-if="s.dateRange?.kind.startsWith('last')" dense outlined type="number" min="1" class="col-3" :label="t('reports.designer.rangeN')"
        :model-value="s.dateRange.n ?? ''" data-testid="date-range-n" @update:model-value="s.dateRange!.n = $event === '' || $event === null ? null : Number($event)"
      />
    </div>
    <div v-if="!s.primaryDateField" class="text-caption text-grey-7">{{ t('reports.designer.rangeNeedsPrimary') }}</div>
  </div>
</template>
