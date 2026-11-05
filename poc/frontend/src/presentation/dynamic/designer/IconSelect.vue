<script lang="ts">
/** Material Icons offered for a module's menu entry; the first one is the default. */
export const MODULE_ICONS = [
  'apps', 'build', 'construction', 'precision_manufacturing', 'engineering', 'factory', 'warehouse', 'inventory_2',
  'local_shipping', 'shopping_cart', 'receipt_long', 'request_quote', 'payments', 'description', 'assignment',
  'fact_check', 'checklist', 'report_problem', 'event', 'schedule', 'people', 'business', 'support_agent', 'settings',
];
</script>

<script setup lang="ts">
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';

defineProps<{ modelValue: string; label: string }>();
const iconSel = useSelectFilter(MODULE_ICONS);
defineEmits<{ 'update:modelValue': [value: string] }>();
</script>

<template>
  <q-select :model-value="modelValue" :options="iconSel.filtered.value" :label="label" :clearable="false" outlined dense
    @update:model-value="(v: string) => $emit('update:modelValue', v)" use-input input-debounce="0" fill-input hide-selected @filter="iconSel.onFilter" @popup-hide="iconSel.reset">
    <template #no-option><SelectNoOption /></template>
    <template #prepend><q-icon :name="modelValue" /></template>
    <template #option="{ itemProps, opt }">
      <q-item v-bind="itemProps">
        <q-item-section avatar><q-icon :name="opt" /></q-item-section>
        <q-item-section>{{ opt }}</q-item-section>
      </q-item>
    </template>
  </q-select>
</template>
