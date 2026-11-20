<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { sameI18n } from 'src/domain/dynamic/i18n-text';
import type { I18nText, Locale } from 'src/domain/dynamic/types';
import { enabledLocales, requiredTextLocales } from 'src/i18n/locale-settings';

/**
 * optional：必填語系都不必填（不顯示 * 也不驗證）；change：任一語系輸入框改完離開時觸發，給「離開才存」的欄位用；
 * original：編輯既有資料時傳載入的值（要是獨立的副本，不能是 v-model 綁的同一個物件）。必填語系只在新增（沒有 original）
 * 或目前文字與 original 不同時才要求，必填語系增加後沒動到的舊資料不先擋。
 */
const props = defineProps<{ modelValue: I18nText; label: string; error?: string; optional?: boolean; hint?: string; maxlength?: number; original?: I18nText }>();
const emit = defineEmits<{ 'update:modelValue': [value: I18nText]; change: [] }>();
const { t } = useI18n();

const required = computed<Locale[]>(() => (props.optional ? [] : requiredTextLocales()));
// 輸入框列出必填語系（不論是否有勾選必填，optional 時仍各列一個）；其餘啟用語系放展開區，停用語系不列但保留原值
const primary = computed<Locale[]>(() => requiredTextLocales());
const others = computed<Locale[]>(() => enabledLocales().filter((l) => !primary.value.includes(l)));
const needed = computed(() => !props.original || !sameI18n(props.modelValue, props.original));
// 以語系為鍵收集目前掛載的必填輸入框；卸載（ref 回 null）時移除，政策更新後不會驗到已消失的舊元件
const inputs = new Map<string, { validate(): boolean | Promise<boolean> }>();
const setInput = (l: string, el: unknown) => { if (el) inputs.set(l, el as never); else inputs.delete(l); };

function set(locale: Locale, v: string | number | null) {
  const next = { ...props.modelValue, [locale]: v == null ? '' : String(v) };
  if (!next[locale]) delete next[locale];
  emit('update:modelValue', next);
}

const rules = (l: Locale) => (required.value.includes(l) && needed.value ? [(v: string) => !!v?.trim() || t('designer.localeRequired', { locale: l })] : []);

defineExpose({
  validate: async () => {
    const results = await Promise.all([...inputs.values()].map((i) => i.validate()));
    return results.every(Boolean);
  },
});
</script>

<template>
  <div>
    <div v-for="(l, i) in primary" :key="l" :data-testid="`i18n-${l}`">
      <q-input
        :ref="(el) => setInput(l, el)" :model-value="modelValue[l] ?? ''"
        :label="required.includes(l) ? `${label} (${l}) *` : `${label} (${l})`"
        :error="(!!error && i === 0) || undefined" :error-message="i === 0 ? error : undefined" :hint="i === 0 ? hint : undefined" :maxlength="maxlength"
        :rules="rules(l)" lazy-rules="ondemand" outlined dense :class="{ 'q-mt-xs': i > 0 }"
        @update:model-value="(v) => set(l, v)" @change="emit('change')"
      />
    </div>
    <q-expansion-item v-if="others.length" dense :label="t('designer.otherLocales')" class="q-mt-xs">
      <q-input
        v-for="l in others" :key="l" :model-value="modelValue[l] ?? ''" :data-testid="`i18n-${l}`"
        :label="`${label} (${l})`" :maxlength="maxlength" outlined dense class="q-mt-xs"
        @update:model-value="(v) => set(l, v)" @change="emit('change')"
      />
    </q-expansion-item>
  </div>
</template>
