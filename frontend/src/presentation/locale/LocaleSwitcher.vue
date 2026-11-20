<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import { computed } from 'vue';
import { applyLocale, type LocaleCode } from 'src/i18n/locale';
import { nativeName, useLocales } from 'src/i18n/locale-settings';

const { t, locale } = useI18n();
const { enabled } = useLocales();
// 只列啟用的語系（政策 locale.enabled），各語言用自稱；停用的不列，目前語系若已停用，下次進來會改用預設語系
const options = computed(() => enabled.value.map((code) => ({ code, label: nativeName(code) })));
const pick = (code: LocaleCode) => applyLocale(code, locale);
</script>

<template>
  <q-btn flat dense round icon="translate" :aria-label="t('locale.title')" data-testid="locale-open">
    <q-tooltip>{{ t('locale.title') }}</q-tooltip>
    <q-menu>
      <q-list dense style="min-width: 140px">
        <q-item
          v-for="l in options" :key="l.code" v-close-popup clickable :active="locale === l.code"
          :lang="l.code" :data-testid="`locale-option-${l.code}`" @click="pick(l.code)"
        >
          <q-item-section>{{ l.label }}</q-item-section>
        </q-item>
      </q-list>
    </q-menu>
  </q-btn>
</template>
