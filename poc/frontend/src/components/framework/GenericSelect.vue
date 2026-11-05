<script setup lang="ts">
import { ref, toRef } from 'vue';
import type { QSelect } from 'quasar';
import SelectNoOption from './SelectNoOption.vue';
import { useSelectFilter } from './useSelectFilter';

export interface SelectOption { value: string; label: string; color?: string; disable?: boolean }

const props = defineProps<{
  modelValue: string | string[] | null | undefined; label: string; options: SelectOption[];
  error?: string; readonly?: boolean; required?: boolean; multiple?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: string | string[] | null] }>();
const { filtered, onFilter, reset } = useSelectFilter(toRef(props, 'options'));
const select = ref<QSelect | null>(null);
function onUpdate(v: string | string[] | null) {
  emit('update:modelValue', v);
  if (props.multiple) select.value?.updateInputValue('');
}
</script>

<template>
  <q-select
    ref="select"
    :model-value="modelValue ?? (multiple ? [] : null)" :options="filtered" :multiple="multiple" :use-chips="multiple"
    use-input input-debounce="0" :fill-input="!multiple" :hide-selected="!multiple" @filter="onFilter" @popup-hide="reset"
    :label="required ? `${label} *` : label" :error="!!error" :error-message="error" :readonly="readonly"
    :clearable="!required && !readonly" emit-value map-options option-value="value" option-label="label"
    option-disable="disable" outlined dense
    @update:model-value="onUpdate"
  >
    <template #no-option><SelectNoOption /></template>
  </q-select>
</template>
