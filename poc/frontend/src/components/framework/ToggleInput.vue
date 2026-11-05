<script setup lang="ts">
defineProps<{ modelValue: boolean | null | undefined; label: string; error?: string; readonly?: boolean; required?: boolean }>();
defineEmits<{ 'update:modelValue': [value: boolean] }>();
</script>

<template>
  <div>
    <q-toggle
      :model-value="!!modelValue" :label="required ? `${label} *` : label" :disable="readonly"
      @update:model-value="(v) => $emit('update:modelValue', v)"
    />
    <div v-if="error" class="text-negative text-caption">{{ error }}</div>
  </div>
</template>
