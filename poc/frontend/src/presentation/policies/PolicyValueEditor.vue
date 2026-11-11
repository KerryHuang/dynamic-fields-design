<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { policyValueText, type PolicyMeta, type PolicyValue } from 'src/domain/dynamic/policy-types';

const props = defineProps<{ meta: PolicyMeta; modelValue: PolicyValue; label: string; disable?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: PolicyValue]; 'update:valid': [valid: boolean] }>();
const { t } = useI18n();

// 數字輸入框保留使用者正在打的文字；只有合法整數才送出，空白時通知呼叫端（update:valid）停用儲存
const text = ref(String(props.modelValue));
watch(() => props.modelValue, (v) => {
  if (props.meta.kind === 'integer' && Number(text.value) !== v) { text.value = String(v); emit('update:valid', true); }
});
function onNumber(v: string | number | null) {
  text.value = v === null ? '' : String(v);
  const n = Number(text.value);
  const valid = text.value.trim() !== '' && Number.isFinite(n);
  emit('update:valid', valid);
  if (valid) emit('update:modelValue', n);
}

const choiceOptions = computed(() => (props.meta.options ?? []).map((o) => ({ value: o, label: t(`policies.choices.${o}`) })));
const hint = computed(() => props.meta.kind === 'integer' && props.meta.unit === 'bytes'
  ? policyValueText(props.meta, Number(text.value) || 0, t) : undefined);
</script>

<template>
  <q-input
    v-if="meta.kind === 'integer'" :model-value="text" type="number" dense outlined :label="label" :hint="hint"
    :min="meta.min" :max="meta.max" :disable="disable" @update:model-value="onNumber"
  />
  <q-toggle
    v-else-if="meta.kind === 'boolean'" :model-value="modelValue as boolean" :label="label" :aria-label="label" :disable="disable"
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  />
  <q-select
    v-else-if="meta.kind === 'choice'" :model-value="modelValue as string" :options="choiceOptions" emit-value map-options dense outlined
    :label="label" :disable="disable" @update:model-value="(v: string) => emit('update:modelValue', v)"
  />
  <div v-else-if="meta.kind === 'roleSet'" role="group" :aria-label="label">
    <div class="text-caption text-muted">{{ label }}</div>
    <q-option-group
      :model-value="modelValue as string[]" :options="choiceOptions" type="checkbox" :disable="disable"
      @update:model-value="(v: string[]) => emit('update:modelValue', (meta.options ?? []).filter((o) => v.includes(o)))"
    />
  </div>
  <q-select
    v-else :model-value="modelValue as string[]" multiple use-chips use-input new-value-mode="add-unique" hide-dropdown-icon dense outlined
    :label="label" :disable="disable" @update:model-value="(v: string[]) => emit('update:modelValue', v)"
  />
</template>
