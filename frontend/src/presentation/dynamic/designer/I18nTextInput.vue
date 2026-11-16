<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { I18nText, Locale } from 'src/domain/dynamic/types';

/** optional：繁中不必填（不顯示 * 也不驗證）；change：任一語系輸入框改完離開時觸發，給「離開才存」的欄位用。 */
const props = defineProps<{ modelValue: I18nText; label: string; error?: string; optional?: boolean; hint?: string; maxlength?: number }>();
const emit = defineEmits<{ 'update:modelValue': [value: I18nText]; change: [] }>();
const { t } = useI18n();
const others: Locale[] = ['en-US', 'th', 'vi'];
const zhInput = ref<{ validate(): boolean | Promise<boolean> } | null>(null);

function set(locale: Locale, v: string | number | null) {
  const next = { ...props.modelValue, [locale]: v == null ? '' : String(v) };
  if (!next[locale]) delete next[locale];
  emit('update:modelValue', next);
}

defineExpose({ validate: async () => !!(await zhInput.value?.validate()) });
</script>

<template>
  <div>
    <div data-testid="i18n-zh-TW">
      <q-input
        ref="zhInput" :model-value="modelValue['zh-TW'] ?? ''" :label="optional ? `${label} (zh-TW)` : `${label} (zh-TW) *`"
        :error="!!error || undefined" :error-message="error" :hint="hint" :maxlength="maxlength"
        :rules="optional ? [] : [(v: string) => !!v?.trim() || t('designer.zhRequired')]" lazy-rules="ondemand" outlined dense
        @update:model-value="(v) => set('zh-TW', v)" @change="emit('change')"
      />
    </div>
    <q-expansion-item dense :label="t('designer.otherLocales')" class="q-mt-xs">
      <q-input
        v-for="l in others" :key="l" :model-value="modelValue[l] ?? ''"
        :label="`${label} (${l})`" :maxlength="maxlength" outlined dense class="q-mt-xs"
        @update:model-value="(v) => set(l, v)" @change="emit('change')"
      />
    </q-expansion-item>
  </div>
</template>
