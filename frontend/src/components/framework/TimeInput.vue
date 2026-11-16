<script setup lang="ts">
const emit = defineEmits<{ 'update:modelValue': [value: string | null] }>();
defineProps<{ modelValue: string | null | undefined; label: string; error?: string; readonly?: boolean; required?: boolean }>();
</script>

<template>
  <q-input
    :model-value="modelValue ?? ''" :label="required ? `${label} *` : label" :error="!!error" :error-message="error"
    :readonly="readonly" mask="##:##" outlined dense
    @update:model-value="(v) => emit('update:modelValue', v === '' || v == null ? null : String(v))"
  >
    <template v-if="!readonly" #append>
      <q-icon name="schedule" class="cursor-pointer">
        <q-popup-proxy cover transition-show="scale" transition-hide="scale">
          <q-time :model-value="modelValue ?? ''" mask="HH:mm" format24h @update:model-value="(v) => emit('update:modelValue', v || null)" />
        </q-popup-proxy>
      </q-icon>
    </template>
  </q-input>
</template>
