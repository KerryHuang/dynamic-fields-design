<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { I18nText, Locale } from 'src/domain/dynamic/types';

const props = defineProps<{ modelValue: I18nText; label: string; error?: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: I18nText] }>();
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
        ref="zhInput" :model-value="modelValue['zh-TW'] ?? ''" :label="`${label} (zh-TW) *`"
        :error="!!error || undefined" :error-message="error"
        :rules="[(v: string) => !!v?.trim() || t('designer.zhRequired')]" lazy-rules="ondemand" outlined dense
        @update:model-value="(v) => set('zh-TW', v)"
      />
    </div>
    <q-expansion-item dense :label="t('designer.otherLocales')" class="q-mt-xs">
      <q-input
        v-for="l in others" :key="l" :model-value="modelValue[l] ?? ''"
        :label="`${label} (${l})`" outlined dense class="q-mt-xs" @update:model-value="(v) => set(l, v)"
      />
    </q-expansion-item>
  </div>
</template>
