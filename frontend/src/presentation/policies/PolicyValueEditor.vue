<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { dynamicRecipientKey, policyValueText, type PolicyMeta, type PolicyValue } from 'src/domain/dynamic/policy-types';
import type { RecipientOption } from './useRecipientNames';

const props = defineProps<{ meta: PolicyMeta; modelValue: PolicyValue; label: string; disable?: boolean; recipientOptions?: RecipientOption[]; recipientsReady?: boolean }>();
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

const offsetOptions = computed(() => (props.meta.options ?? []).map((o) => ({ value: o, label: `UTC${o}` })));
const choiceOptions = computed(() => (props.meta.options ?? []).map((o) => ({ value: o, label: t(`policies.choices.${o}`) })));

// 收件人：選項＝呼叫端給的清單，加上已選但清單裡找不到的項目（已停用或不存在），標「找不到或已停用」；
// 名稱還沒載入完（recipientsReady 為 false）時清單是空的，已選項目顯示原始代碼，不誤報
const recipientFilter = ref('');
// 動態項目（例：ruleEditor）不是帳號或群組，另用勾選表示；值仍在同一個陣列裡，計入項目數
const dynamicItems = computed(() => props.meta.dynamicRecipients ?? []);
const isDynamic = (v: string) => dynamicItems.value.includes(v);
const fixedRecipients = computed(() => (props.modelValue as string[]).filter((v) => !isDynamic(v)));
const recipientAll = computed<RecipientOption[]>(() => {
  const base = props.recipientOptions ?? [];
  const known = new Set(base.map((o) => o.value));
  const missing = fixedRecipients.value.filter((v) => !known.has(v))
    .map((v) => ({ value: v, label: props.recipientsReady ? t('policies.recipients.missingItem', { item: v }) : v }));
  return [...base, ...missing];
});
const recipientShown = computed(() => {
  const q = recipientFilter.value.trim().toLowerCase();
  return q ? recipientAll.value.filter((o) => o.label.toLowerCase().includes(q)) : recipientAll.value;
});
const recipientsValid = (v: string[]) => v.length >= (props.meta.min ?? 1) && v.length <= (props.meta.max ?? 20);
watch(() => props.modelValue, (v) => { if (props.meta.kind === 'recipients') emit('update:valid', recipientsValid(v as string[])); });
function onRecipients(v: string[]) {
  const next = [...(props.modelValue as string[]).filter(isDynamic), ...v];
  emit('update:valid', recipientsValid(next));
  emit('update:modelValue', next);
}
function onDynamic(item: string, on: boolean) {
  const rest = (props.modelValue as string[]).filter((v) => v !== item);
  const next = on ? [item, ...rest] : rest;
  emit('update:valid', recipientsValid(next));
  emit('update:modelValue', next);
}
function onRecipientFilter(v: string, update: (fn: () => void) => void) {
  update(() => { recipientFilter.value = v; });
}
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
  <q-select
    v-else-if="meta.kind === 'utcOffset'" :model-value="modelValue as string" :options="offsetOptions" emit-value map-options dense outlined
    :label="label" :aria-label="label" :disable="disable" @update:model-value="(v: string) => emit('update:modelValue', v)"
  />
  <div v-else-if="meta.kind === 'roleSet'" role="group" :aria-label="label">
    <div class="text-caption text-muted">{{ label }}</div>
    <q-option-group
      :model-value="modelValue as string[]" :options="choiceOptions" type="checkbox" :disable="disable"
      @update:model-value="(v: string[]) => emit('update:modelValue', (meta.options ?? []).filter((o) => v.includes(o)))"
    />
  </div>
  <div v-else-if="meta.kind === 'recipients'" class="column no-wrap q-gutter-y-xs">
    <q-select
      :model-value="fixedRecipients" :options="recipientShown" multiple use-chips use-input
      emit-value map-options dense outlined :label="label" :aria-label="label" :placeholder="t('policies.recipients.search')" :disable="disable" @filter="onRecipientFilter"
      @update:model-value="onRecipients"
    />
    <q-checkbox
      v-for="item in dynamicItems" :key="item" dense :model-value="(modelValue as string[]).includes(item)" :label="t(dynamicRecipientKey(item))"
      :disable="disable" :data-testid="`policy-dynamic-${item}`" @update:model-value="(v: boolean) => onDynamic(item, v)"
    />
  </div>
  <q-select
    v-else :model-value="modelValue as string[]" multiple use-chips use-input new-value-mode="add-unique" hide-dropdown-icon dense outlined
    :label="label" :disable="disable" @update:model-value="(v: string[]) => emit('update:modelValue', v)"
  />
</template>
