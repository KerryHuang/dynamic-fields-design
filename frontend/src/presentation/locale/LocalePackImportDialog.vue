<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useApplyLocalePack, useEnableLocale, usePreviewLocalePack } from 'src/application/dynamic/queries/locale-packs-queries';
import type { ApplyLocalePackResult, LocalePackReportData } from 'src/domain/dynamic/locale-pack-types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import LocalePackReport from './LocalePackReport.vue';

// 匯入語系包：選檔 → 預覽（報告）→ 套用 →（新語系未啟用時）現在啟用。預覽過期或被別人改過（409）時用同一個檔重新預覽。
const props = defineProps<{ modelValue: boolean; importEnabled: boolean; packMaxBytes: number }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
const { t } = useI18n();
const $q = useQuasar();

const preview = usePreviewLocalePack();
const apply = useApplyLocalePack();
const enable = useEnableLocale();

const fileInput = ref<HTMLInputElement | null>(null);
const picked = ref<{ name: string; bytes: ArrayBuffer } | null>(null);
const report = ref<LocalePackReportData | null>(null);
const result = ref<ApplyLocalePackResult | null>(null);
const enabledNow = ref(false);
const error = ref<string | null>(null);
const canRepreview = ref(false);
const busy = ref<'preview' | 'apply' | null>(null);

// 換檔、重新預覽、關閉或打開對話框都會推進世代；每個 await 回來先核對世代，舊流程的結果與錯誤一律不寫回（第二輪審查必修 1）
let generation = 0;
const isCurrent = (gen: number) => gen === generation;

function reset() {
  generation++;
  picked.value = null; report.value = null; result.value = null; enabledNow.value = false; error.value = null; canRepreview.value = false; busy.value = null;
}
watch(() => props.modelValue, reset);

/** 檔案大小上限以 MB 顯示（最多一位小數）。 */
const sizeText = computed(() => String(Math.round((props.packMaxBytes / 1048576) * 10) / 10));
const tooLarge = () => t('localePacks.dialog.tooLarge', { size: sizeText.value });
const messageOf = (e: unknown, fallbackKey: string) => {
  const err = e as ApiError;
  if (err.status === 413) return tooLarge();
  return leftoverMessages(err, []).join('；') || t(fallbackKey);
};

async function runPreview() {
  if (!picked.value) return;
  const gen = ++generation;
  error.value = null; canRepreview.value = false; report.value = null; result.value = null; enabledNow.value = false; busy.value = 'preview';
  try {
    const r = await preview.mutateAsync({ bytes: picked.value.bytes, fileName: picked.value.name });
    if (isCurrent(gen)) report.value = r;
  } catch (e) {
    if (isCurrent(gen)) error.value = messageOf(e, 'localePacks.dialog.previewFailed');
  } finally {
    if (isCurrent(gen)) busy.value = null;
  }
}

async function onFile(ev: Event) {
  const input = ev.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  reset();
  const gen = generation;
  input.value = '';
  // 送出一定會被後端擋才先擋（frontend.md）：上限來自清單回應，不在前端存常數
  if (file.size > props.packMaxBytes) {
    error.value = tooLarge();
    return;
  }
  const bytes = await file.arrayBuffer();
  if (!isCurrent(gen)) return;
  picked.value = { name: file.name, bytes };
  await runPreview();
}

async function doApply() {
  if (!report.value?.previewId) return;
  const gen = generation;
  error.value = null; busy.value = 'apply';
  try {
    const r = await apply.mutateAsync({ previewId: report.value.previewId, fileHash: report.value.fileHash });
    if (isCurrent(gen)) result.value = r;
  } catch (e) {
    if (!isCurrent(gen)) return;
    const err = e as ApiError;
    canRepreview.value = err.code === 'localePackStale' || err.code === 'localePackPreviewExpired';
    error.value = messageOf(e, 'localePacks.dialog.applyFailed');
  } finally {
    if (isCurrent(gen)) busy.value = null;
  }
}

async function doEnable() {
  if (!result.value) return;
  const gen = generation;
  error.value = null;
  try {
    await enable.mutateAsync(result.value.code);
    if (!isCurrent(gen)) return;
    enabledNow.value = true;
    $q.notify({ type: 'positive', message: t('localePacks.dialog.enabledDone', { name: report.value?.locale.nativeName ?? result.value.code }) });
  } catch (e) {
    if (isCurrent(gen)) error.value = messageOf(e, 'localePacks.failed');
  }
}

const close = () => emit('update:modelValue', false);
const applyDisabled = computed(() => !report.value?.previewId || !props.importEnabled);
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="emit('update:modelValue', $event)">
    <q-card style="width: 720px; max-width: 100%" data-testid="pack-dialog">
      <q-card-section class="text-subtitle1">{{ t('localePacks.dialog.title') }}</q-card-section>
      <q-card-section class="scroll" style="max-height: 70vh">
        <p class="text-muted">{{ t('localePacks.dialog.hint') }}</p>
        <input ref="fileInput" type="file" accept=".json,application/json" class="hidden" data-testid="pack-file" @change="onFile" />
        <q-btn outline no-caps icon="upload_file" :label="t('localePacks.dialog.pick')" :disable="!!result || !!busy" data-testid="pack-pick" @click="fileInput?.click()" />
        <span v-if="picked" class="q-ml-sm text-caption" style="overflow-wrap: anywhere">{{ picked.name }}</span>

        <div v-if="busy === 'preview'" class="q-mt-md row items-center q-gutter-sm" data-testid="pack-loading">
          <q-spinner size="20px" /><span>{{ t('localePacks.dialog.previewing') }}</span>
        </div>
        <div v-if="error" class="q-mt-md text-negative" data-testid="pack-error">
          {{ error }}
          <q-btn v-if="canRepreview" flat dense no-caps color="primary" class="q-ml-sm" :label="t('localePacks.dialog.repreview')" data-testid="pack-repreview" @click="runPreview" />
        </div>
        <div v-if="report && (!importEnabled || !report.previewId)" class="q-mt-md text-warning" data-testid="pack-import-disabled">
          {{ t('localePacks.dialog.importDisabled') }}
        </div>
        <LocalePackReport v-if="report" :report="report" class="q-mt-md" />

        <div v-if="result" class="q-mt-md" data-testid="pack-done">
          <div class="text-positive">
            {{ t(result.noChanges ? 'localePacks.dialog.noChanges' : 'localePacks.dialog.applied', { name: report?.locale.nativeName ?? result.code }) }}
          </div>
          <q-btn v-if="!result.enabled && !enabledNow" class="q-mt-sm" color="primary" no-caps :label="t('localePacks.dialog.enableNow')"
            :loading="enable.isPending.value" data-testid="pack-enable" @click="doEnable" />
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="result ? t('localePacks.dialog.close') : t('dynamic.cancel')" data-testid="pack-close" @click="close" />
        <q-btn v-if="!result" color="primary" no-caps :label="t('localePacks.dialog.apply')" :disable="applyDisabled" :loading="busy === 'apply'"
          data-testid="pack-apply" @click="doApply" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
