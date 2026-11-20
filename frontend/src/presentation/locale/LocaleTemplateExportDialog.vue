<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { LocaleAdminEntry, LocaleTemplateOptions } from 'src/domain/dynamic/locale-pack-types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 匯出翻譯樣板（語系包設計 §7.1；i4d2）：target 是已安裝語系時固定顯示；null 是「新語系樣板」，要自己填代碼與自稱。
// 驗證全在後端；這裡只擋一定會被擋的（範圍都不勾、代碼空白）。樣板大於語系包大小上限時照樣下載，只提示（Q8）。
const props = defineProps<{ modelValue: boolean; target: LocaleAdminEntry | null; locales: LocaleAdminEntry[]; packMaxBytes: number }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
const { t } = useI18n();
const $q = useQuasar();

const scopeUi = ref(true);
const scopeServer = ref(true);
const onlyMissing = ref(false);
const onlyChanged = ref(false);
const source = ref('zh-TW');
const reference = ref('en-US');
const code = ref('');
const nativeName = ref('');
const fallback = ref('');
const quasarLang = ref('');
const busy = ref(false);
const error = ref<string | null>(null);
const tooLarge = ref<{ size: string; max: string } | null>(null);

function reset() {
  scopeUi.value = true; scopeServer.value = true; onlyMissing.value = false; onlyChanged.value = false;
  source.value = 'zh-TW'; reference.value = 'en-US';
  code.value = ''; nativeName.value = ''; fallback.value = ''; quasarLang.value = '';
  busy.value = false; error.value = null; tooLarge.value = null;
}
watch(() => props.modelValue, (open) => { if (open) reset(); }, { immediate: true });

const isNew = computed(() => !props.target);
const targetCode = computed(() => props.target?.code ?? code.value.trim());
const choices = computed(() => props.locales.map((l) => ({ value: l.code, label: `${l.nativeName}（${l.code}）` })));
const referenceChoices = computed(() => [{ value: '', label: t('localePacks.export.referenceNone') }, ...choices.value]);
const fallbackChoices = computed(() => [{ value: '', label: t('localePacks.export.fallbackNone') }, ...choices.value]);
// 只匯出變動句子的前提：目標語系現在有原文已更新或參數不符的句子（新語系沒有目前版，一定是 0）
const changedCount = computed(() => {
  const r = props.target?.review;
  return r ? r.ui.baseChanged + r.ui.paramsChanged + r.server.baseChanged + r.server.paramsChanged : 0;
});
const canDownload = computed(() => (scopeUi.value || scopeServer.value) && targetCode.value !== '' && !busy.value);

/** 以 MB 顯示，最多兩位小數（上限很小時一位小數會四捨五入成 0）。 */
const mb = (bytes: number) => String(Math.round((bytes / 1048576) * 100) / 100);

async function doDownload() {
  const options: LocaleTemplateOptions = {
    scopes: [...(scopeUi.value ? ['ui' as const] : []), ...(scopeServer.value ? ['server' as const] : [])],
    onlyMissing: onlyMissing.value,
    onlyBaseChanged: onlyChanged.value,
    source: source.value,
    reference: reference.value,
    ...(isNew.value ? {
      nativeName: nativeName.value,
      ...(fallback.value ? { fallback: fallback.value } : {}),
      ...(quasarLang.value.trim() ? { quasarLang: quasarLang.value.trim() } : {}),
    } : {}),
  };
  busy.value = true; error.value = null; tooLarge.value = null;
  try {
    const { blob, fileName } = await dynamicApi.exportLocaleTemplate(targetCode.value, options);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    if (blob.size > props.packMaxBytes) {
      tooLarge.value = { size: mb(blob.size), max: mb(props.packMaxBytes) };
      return;
    }
    $q.notify({ type: 'positive', message: t('localePacks.export.done', { file: fileName }) });
    emit('update:modelValue', false);
  } catch (e) {
    error.value = isForbidden(e) ? t('dynamic.forbidden') : (leftoverMessages(e as ApiError, []).join('；') || t('localePacks.export.failed'));
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="emit('update:modelValue', $event)">
    <q-card style="width: 560px; max-width: 100%" data-testid="export-dialog">
      <q-card-section class="text-subtitle1">{{ t('localePacks.export.title') }}</q-card-section>
      <q-card-section class="scroll" style="max-height: 70vh">
        <div v-if="target" class="q-mb-md" style="overflow-wrap: anywhere" data-testid="export-target">
          <span class="text-muted">{{ t('localePacks.export.target') }}：</span>
          <span class="text-weight-bold">{{ target.nativeName }}</span>
          <span class="text-caption text-muted q-ml-sm">{{ target.code }}</span>
        </div>
        <template v-else>
          <q-input v-model="code" dense outlined class="q-mb-sm" :label="t('localePacks.export.code')" data-testid="export-code" />
          <q-input v-model="nativeName" dense outlined class="q-mb-sm" :label="t('localePacks.export.nativeName')" data-testid="export-native-name" />
          <q-select v-model="fallback" dense outlined emit-value map-options class="q-mb-sm" :options="fallbackChoices"
            :label="t('localePacks.export.fallback')" data-testid="export-fallback" />
          <q-input v-model="quasarLang" dense outlined class="q-mb-md" :label="t('localePacks.export.quasarLang')" data-testid="export-quasar-lang" />
        </template>

        <q-select v-model="source" dense outlined emit-value map-options class="q-mb-sm" :options="choices"
          :label="t('localePacks.export.source')" data-testid="export-source" />
        <q-select v-model="reference" dense outlined emit-value map-options class="q-mb-md" :options="referenceChoices"
          :label="t('localePacks.export.reference')" data-testid="export-reference" />

        <div class="text-weight-medium">{{ t('localePacks.export.scopes') }}</div>
        <div class="column no-wrap">
          <q-checkbox v-model="scopeUi" dense :label="t('localePacks.report.sections.ui')" data-testid="export-scope-ui" />
          <q-checkbox v-model="scopeServer" dense :label="t('localePacks.report.sections.server')" data-testid="export-scope-server" />
          <q-checkbox v-model="onlyMissing" dense class="q-mt-sm" :label="t('localePacks.export.onlyMissing')" data-testid="export-only-missing" />
          <q-checkbox v-model="onlyChanged" dense :disable="changedCount === 0" :label="t('localePacks.export.onlyChanged')" data-testid="export-only-changed" />
        </div>

        <p class="text-caption text-muted q-mt-md q-mb-none">{{ t('localePacks.export.hint') }}</p>
        <div v-if="error" class="q-mt-md text-negative" style="overflow-wrap: anywhere" data-testid="export-error">{{ error }}</div>
        <div v-if="tooLarge" class="q-mt-md text-warning" style="overflow-wrap: anywhere" data-testid="export-too-large">
          {{ t('localePacks.export.tooLarge', tooLarge) }}
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="t('dynamic.cancel')" data-testid="export-cancel" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" no-caps icon="download" :label="t('localePacks.export.download')" :disable="!canDownload" :loading="busy"
          data-testid="export-download" @click="doDownload" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
