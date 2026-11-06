<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import type { MapRow } from 'src/domain/dynamic/conversion';

type Option = { value: number; label: string };
const props = defineProps<{
  modelValue: MapRow[];
  fromOptions: Option[];
  toOptions: Option[];
  fromLabel: string;
  toLabel: string;
  disable?: boolean;
  error?: string;
}>();
const emit = defineEmits<{ 'update:modelValue': [rows: MapRow[]] }>();
const { t } = useI18n();

const fromSel = useSelectFilter<Option>([]);
const toSel = useSelectFilter<Option>([]);

const setRow = (i: number, patch: Partial<MapRow>) =>
  emit('update:modelValue', props.modelValue.map((r, j) => (j === i ? { ...r, ...patch } : r)));
const addRow = () => emit('update:modelValue', [...props.modelValue, { from: null, to: null }]);
const removeRow = (i: number) => emit('update:modelValue', props.modelValue.filter((_, j) => j !== i));
</script>

<template>
  <div class="q-gutter-y-sm" data-testid="field-map">
    <div v-if="!modelValue.length" class="text-caption text-muted">{{ t('docflow.map.empty') }}</div>
    <div v-for="(row, i) in modelValue" :key="i" class="row no-wrap items-center q-gutter-x-sm" :data-testid="`map-row-${i}`">
      <q-select
        class="col" :model-value="row.from" :options="fromSel.filterBy(fromOptions)" :label="fromLabel" :disable="disable"
        emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="`map-from-${i}`"
        @filter="fromSel.onFilter" @popup-hide="fromSel.reset" @update:model-value="(v: number) => setRow(i, { from: v })"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>
      <q-icon name="arrow_forward" />
      <q-select
        class="col" :model-value="row.to" :options="toSel.filterBy(toOptions)" :label="toLabel" :disable="disable"
        emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="`map-to-${i}`"
        @filter="toSel.onFilter" @popup-hide="toSel.reset" @update:model-value="(v: number) => setRow(i, { to: v })"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>
      <q-btn flat dense round icon="delete" :title="t('docflow.map.remove')" :disable="disable" :data-testid="`map-remove-${i}`" @click="removeRow(i)" />
    </div>
    <div v-if="error" class="text-caption text-negative" data-testid="map-error">{{ error }}</div>
    <q-btn flat dense color="primary" icon="add" :label="t('docflow.map.add')" :disable="disable" data-testid="map-add" @click="addRow" />
  </div>
</template>
