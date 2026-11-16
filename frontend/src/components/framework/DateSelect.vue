<script setup lang="ts">
import { computed } from 'vue';

// withTime: native datetime-local input; emits yyyy-MM-ddTHH:mm:ss+08:00 (Asia/Taipei, same as backend).
const props = defineProps<{ modelValue: string | null | undefined; label: string; error?: string; readonly?: boolean; required?: boolean; withTime?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: string | null] }>();
const shown = computed(() => (props.withTime ? (props.modelValue ?? '').slice(0, 16) : (props.modelValue ?? '')));

function onInput(v: string | number | null) {
  if (v === '' || v == null) return emit('update:modelValue', null);
  emit('update:modelValue', props.withTime ? `${v}:00+08:00` : String(v));
}
</script>

<template>
  <q-input
    :model-value="shown" :label="required ? `${label} *` : label" :error="!!error" :error-message="error"
    :readonly="readonly" :type="withTime ? 'datetime-local' : undefined" :mask="withTime ? undefined : '####-##-##'"
    :stack-label="withTime" outlined dense @update:model-value="onInput"
  >
    <template v-if="!withTime && !readonly" #append>
      <q-icon name="event" class="cursor-pointer">
        <q-popup-proxy cover transition-show="scale" transition-hide="scale">
          <q-date :model-value="modelValue ?? ''" mask="YYYY-MM-DD" @update:model-value="(v) => emit('update:modelValue', v || null)" />
        </q-popup-proxy>
      </q-icon>
    </template>
  </q-input>
</template>
