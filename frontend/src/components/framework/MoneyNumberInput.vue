<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{ modelValue: number | null | undefined; label: string; error?: string; readonly?: boolean; required?: boolean; scale?: number }>();
const emit = defineEmits<{ 'update:modelValue': [value: number | null] }>();
const step = computed(() => (props.scale ? 1 / 10 ** props.scale : 1));

function onInput(v: string | number | null) {
  if (v === '' || v == null) return emit('update:modelValue', null);
  const n = Number(v);
  emit('update:modelValue', Number.isNaN(n) ? null : n);
}
</script>

<template>
  <q-input
    :model-value="modelValue ?? ''" type="number" :step="step" :label="required ? `${label} *` : label"
    :error="!!error" :error-message="error" :readonly="readonly" outlined dense @update:model-value="onInput"
  />
</template>
