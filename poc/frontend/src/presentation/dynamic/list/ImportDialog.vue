<script setup lang="ts">
import { Notify, type QTableColumn } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useCommitImport } from 'src/application/dynamic/queries/records-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ImportPreview, ImportRowError, ModuleSchema } from 'src/domain/dynamic/types';
import { errorFor } from 'src/presentation/dynamic/designer/api-error';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import { isForbidden, type ApiError } from 'src/services/api/http';

const props = defineProps<{ schema: ModuleSchema; modelValue: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
const { t, locale } = useI18n();
const commit = useCommitImport();

const file = ref<File | null>(null);
const preview = ref<ImportPreview>();
const loading = ref(false);
const fileError = ref<string>();
/** Row errors from the preview, replaced by the server's rows[{line}].{key} errors when the commit is rejected. */
const rowErrors = ref<ImportRowError[]>([]);
/** Only the latest selection may write results; an older, slower preview is dropped. */
let seq = 0;
/** Made when a file is picked: confirming it again after a failure reuses it, another file replaces it. */
let idempotencyKey = newIdempotencyKey();

function reset() {
  seq++;
  file.value = null;
  preview.value = undefined;
  loading.value = false;
  fileError.value = undefined;
  rowErrors.value = [];
}
watch(() => props.modelValue, (open) => { if (open) reset(); });

const messageOf = (e: ApiError) =>
  isForbidden(e) ? t('dynamic.forbidden') : errorFor(e, 'file') ?? e.message ?? t('dynamic.loadFailed');

async function onFile(f: File | null) {
  reset();
  file.value = f;
  if (!f) return;
  idempotencyKey = newIdempotencyKey();
  const mine = seq;
  loading.value = true;
  try {
    const p = await dynamicApi.previewImport(props.schema.key, f);
    if (mine !== seq) return;
    preview.value = p;
    rowErrors.value = p.errors;
  } catch (e) {
    if (mine === seq) fileError.value = messageOf(e as ApiError);
  } finally {
    if (mine === seq) loading.value = false;
  }
}

const labels = computed(() => new Map(props.schema.blocks.flatMap((b) => b.fields).map((f) => [f.key, resolveI18n(f.label, locale.value)])));
const labelOf = (key: string) => (key === '_record' ? t('dynamic.import.wholeRecord') : labels.value.get(key) ?? key);
const mappedLabel = (key?: string) => (key ? labelOf(key) : undefined);

const columnRows = computed(() => (preview.value?.columns ?? []).map((c, i) => ({
  id: i, header: c.header, target: mappedLabel(c.fieldKey) ?? c.ignoredReason ?? '',
})));
const errorRows = computed(() => rowErrors.value.flatMap((r) =>
  Object.entries(r.errors).map(([key, msgs]) => ({ id: `${r.line}:${key}`, line: r.line, field: labelOf(key), message: msgs.join('、') }))));

const columnCols = computed<QTableColumn[]>(() => [
  { name: 'header', label: t('dynamic.import.fileColumn'), field: 'header', align: 'left' },
  { name: 'target', label: t('dynamic.import.mappedField'), field: 'target', align: 'left' },
]);
const errorCols = computed<QTableColumn[]>(() => [
  { name: 'line', label: t('dynamic.import.line'), field: 'line', align: 'left' },
  { name: 'field', label: t('dynamic.import.field'), field: 'field', align: 'left' },
  { name: 'message', label: t('dynamic.import.message'), field: 'message', align: 'left' },
]);

const hasErrors = computed(() => rowErrors.value.length > 0);
const canConfirm = computed(() => !!preview.value && !loading.value && !hasErrors.value && !commit.isPending.value && preview.value.valid > 0);

/** `rows[12].f_3` → line 12, key f_3. */
function rowsFromCommitErrors(errors: Record<string, string[]>): { rows: ImportRowError[]; rest: string[] } {
  const byLine = new Map<number, Record<string, string[]>>();
  const rest: string[] = [];
  for (const [k, msgs] of Object.entries(errors)) {
    const m = /^rows\[(\d+)\]\.(.+)$/.exec(k);
    if (!m) { rest.push(...msgs); continue; }
    const line = Number(m[1]);
    byLine.set(line, { ...byLine.get(line), [m[2]!]: msgs });
  }
  return { rows: [...byLine].sort((a, b) => a[0] - b[0]).map(([line, errs]) => ({ line, errors: errs })), rest };
}

function confirm() {
  if (!file.value || !canConfirm.value) return;
  commit.mutate({ moduleKey: props.schema.key, file: file.value, idempotencyKey }, {
    onSuccess: (r) => {
      Notify.create({ type: 'positive', message: t('dynamic.import.done', { n: r.created }) });
      emit('update:modelValue', false);
    },
    onError: (e) => {
      const err = e as unknown as ApiError;
      const { rows, rest } = rowsFromCommitErrors(err.errors ?? {});
      if (rows.length) rowErrors.value = rows;
      if (rest.length) fileError.value = rest.join('、');
      else if (!rows.length) fileError.value = messageOf(err);
    },
  });
}
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="min-width: 36rem; max-width: 90vw">
      <q-card-section class="text-h6">{{ t('dynamic.import.title') }}</q-card-section>
      <q-card-section class="q-gutter-sm">
        <q-file
          :model-value="file" accept=".csv" outlined dense clearable data-testid="import-file"
          :label="t('dynamic.import.chooseFile')" @update:model-value="onFile"
        />
        <div v-if="fileError" class="text-negative" data-testid="import-file-error">{{ fileError }}</div>
        <q-linear-progress v-if="loading" indeterminate />
        <template v-if="preview">
          <q-table
            flat bordered dense hide-pagination row-key="id" :rows="columnRows" :columns="columnCols"
            :rows-per-page-options="[0]" data-testid="import-columns"
          />
          <div data-testid="import-summary">{{ t('dynamic.import.summary', { total: preview.total, valid: preview.valid }) }}</div>
          <template v-if="hasErrors">
            <q-table
              flat bordered dense hide-pagination row-key="id" :rows="errorRows" :columns="errorCols"
              :rows-per-page-options="[0]" data-testid="import-errors"
            />
            <div class="text-negative">{{ t('dynamic.import.fixAndReselect') }}</div>
          </template>
        </template>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
        <q-btn color="primary" data-testid="import-confirm" :disable="!canConfirm" :loading="commit.isPending.value" :label="t('dynamic.import.confirm')" @click="confirm" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
