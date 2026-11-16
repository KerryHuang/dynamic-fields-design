<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import { LOCALES, applyLocale, type LocaleCode } from 'src/i18n/locale';

const { t, locale } = useI18n();
const pick = (code: LocaleCode) => applyLocale(code, locale);
</script>

<template>
  <q-btn flat dense round icon="translate" :aria-label="t('locale.title')" data-testid="locale-open">
    <q-tooltip>{{ t('locale.title') }}</q-tooltip>
    <q-menu>
      <q-list dense style="min-width: 140px">
        <q-item
          v-for="l in LOCALES" :key="l.code" v-close-popup clickable :active="locale === l.code"
          :lang="l.code" :data-testid="`locale-option-${l.code}`" @click="pick(l.code)"
        >
          <q-item-section>{{ l.label }}</q-item-section>
        </q-item>
      </q-list>
    </q-menu>
  </q-btn>
</template>
