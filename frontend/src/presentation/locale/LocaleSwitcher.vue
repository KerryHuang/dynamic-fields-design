<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import { computed, ref } from 'vue';
import type { LocaleCode } from 'src/i18n/locale';
import { setPreviewLocale } from 'src/i18n/locale-preview';
import { installedLocales, nativeName, useLocales } from 'src/i18n/locale-settings';
import { switchLocale } from 'src/i18n/ui-messages';

// allowPreview：系統管理員才傳 true（MainLayout 以 /me 判斷）；元件本身不查身分，登入頁不會多一次 /me（i4b 判斷 17）
const props = withDefaults(defineProps<{ allowPreview?: boolean }>(), { allowPreview: false });
const composer = useI18n({ useScope: 'global' });
const { t, locale } = composer;
const { enabled } = useLocales();
// 只列啟用的語系（政策 locale.enabled），各語言用自稱；停用的不列，目前語系若已停用，下次進來會改用預設語系
const options = computed(() => enabled.value.map((code) => ({ code, label: nativeName(code) })));
// 試看：已安裝但未啟用的語系（M2）
const previews = computed(() => (props.allowPreview ? installedLocales().filter((c) => !enabled.value.includes(c)) : [])
  .map((code) => ({ code, label: `${nativeName(code)}（${t('locale.notEnabled')}）` })));
const menu = ref(false);
const loading = ref<LocaleCode | null>(null);
const pick = async (code: LocaleCode, preview = false) => {
  if (loading.value) return;
  setPreviewLocale(preview ? code : null);
  loading.value = code;
  try {
    await switchLocale({ global: composer }, code, { preview });
  } finally {
    loading.value = null;
    menu.value = false;
  }
};
</script>

<template>
  <q-btn flat dense round icon="translate" :aria-label="t('locale.title')" data-testid="locale-open">
    <q-tooltip>{{ t('locale.title') }}</q-tooltip>
    <q-menu v-model="menu">
      <q-list dense style="min-width: 140px">
        <q-item
          v-for="l in options" :key="l.code" clickable :active="locale === l.code" :disable="loading !== null && loading !== l.code"
          :lang="l.code" :data-testid="`locale-option-${l.code}`" @click="pick(l.code)"
        >
          <q-item-section>{{ l.label }}</q-item-section>
          <q-item-section v-if="loading === l.code" side><q-spinner size="1em" :aria-label="t('locale.loading')" /></q-item-section>
        </q-item>
        <template v-if="previews.length">
          <q-separator data-testid="locale-preview-separator" />
          <q-item
            v-for="l in previews" :key="l.code" clickable :active="locale === l.code" :disable="loading !== null && loading !== l.code"
            :lang="l.code" :data-testid="`locale-preview-${l.code}`" @click="pick(l.code, true)"
          >
            <q-item-section>{{ l.label }}</q-item-section>
            <q-item-section v-if="loading === l.code" side><q-spinner size="1em" :aria-label="t('locale.loading')" /></q-item-section>
          </q-item>
        </template>
      </q-list>
    </q-menu>
  </q-btn>
</template>
