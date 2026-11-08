<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { useForm } from 'vee-validate';
import { computed, nextTick, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useConversionInputs, useConvertRecord } from 'src/application/dynamic/queries/conversions-queries';
import { fetchModuleSchema, useModuleSchema } from 'src/application/dynamic/queries/schema-queries';
import { canConvert, convertLines, defaultQuantities, quantityErrors, serverQuantityErrors } from 'src/domain/dynamic/conversion';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConvertBody, RecordConversion } from 'src/domain/dynamic/types';
import DynamicField from 'src/presentation/dynamic/components/DynamicField.vue';
import { messagesOutside } from 'src/presentation/dynamic/designer/api-error';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ modelValue: boolean; recordId: number; rowVersion: string; conversion: RecordConversion }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const router = useRouter();
const qc = useQueryClient();
const convert = useConvertRecord();

const QUANTITY_KEY = /^lines\[\d+\]\.quantity$/;
const lines = computed(() => props.conversion.lines);

// Required target header fields the copy cannot supply: the user fills them here (sent as `values`, under their own write permission).
const inputsQuery = useConversionInputs(computed(() => props.recordId), computed(() => props.conversion.key), computed(() => props.modelValue));
// Fields the rules made required only after the user filled something (or that the first query could not know about) join the list
// when the server answers 400 for them; no extra API call per keystroke.
const extraKeys = ref<string[]>([]);
const inputKeys = computed(() => [...new Set([...(inputsQuery.data.value ?? []), ...extraKeys.value])]);
const schemaQuery = useModuleSchema(computed(() => props.conversion.targetModuleKey), computed(() => inputKeys.value.length > 0));
const inputFields = computed(() => {
  const keys = new Set(inputKeys.value);
  return (schemaQuery.data.value?.blocks ?? []).flatMap((b) => b.fields).filter((f) => keys.has(f.key));
});
// The user may add but not read the target (schema is 403): say so instead of loading forever.
// The schema only matters when there is something to render: a failed schema fetch (cache shared with showServerFieldErrors) must not lock the dialog otherwise.
const loadFailed = computed(() => inputsQuery.isError.value || (inputKeys.value.length > 0 && schemaQuery.isError.value));
const { values: formValues, setErrors, resetForm } = useForm<Record<string, unknown>>();
const isBlank = (v: unknown) => v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0);
const inputsReady = computed(() => !loadFailed.value && !inputsQuery.isPending.value && (!inputKeys.value.length || inputFields.value.length === inputKeys.value.length));
// Only what the inputs endpoint called required blocks convert; a field added by a 400 may be optional (the server says again if it is not).
const requiredKeys = computed(() => new Set(inputsQuery.data.value ?? []));
const inputsFilled = computed(() => inputFields.value.every((f) => !requiredKeys.value.has(f.key) || !isBlank(formValues[f.key])));
const quantities = reactive<Record<number, number | null>>({});
const serverErrors = ref(new Map<number, string>());
// New per opened dialog; a resend after a failure reuses it, a success or a 422 (same key, other content) replaces it.
let idempotencyKey = newIdempotencyKey();
watch(() => [props.modelValue, props.conversion] as const, ([open]) => {
  if (!open) return;
  idempotencyKey = newIdempotencyKey();
  serverErrors.value = new Map();
  resetForm({ values: {} });
  extraKeys.value = [];
  for (const k of Object.keys(quantities)) delete quantities[Number(k)];
  Object.assign(quantities, defaultQuantities(lines.value ?? []));
}, { immediate: true });

const errors = computed(() => quantityErrors(lines.value ?? [], quantities));
const errorText = (lineId: number) => {
  const e = errors.value.get(lineId);
  return e ? t(`docflow.convert.error.${e}`) : serverErrors.value.get(lineId);
};
// A header conversion has no lines to check; a line conversion needs at least one line above 0.
const valid = computed(() => (lines.value ? canConvert(lines.value, quantities) : true) && inputsReady.value && inputsFilled.value);

const setQuantity = (lineId: number, v: string | number | null) => {
  serverErrors.value.delete(lineId);
  quantities[lineId] = v === '' || v === null ? null : Number(v);
};

function reload() {
  emit('update:modelValue', false);
  return Promise.all([
    qc.invalidateQueries({ queryKey: ['record', props.recordId] }),
    qc.invalidateQueries({ queryKey: ['record-conversions', props.recordId] }),
  ]);
}

/** Puts server errors on the fields they belong to; a target field not asked for yet is added to the list first. */
async function showServerFieldErrors(errors: Record<string, string[]>) {
  const candidates = Object.keys(errors).filter((k) => !QUANTITY_KEY.test(k));
  if (!candidates.length) return; // quantity errors belong to the lines table; no schema needed
  const schema = await fetchModuleSchema(qc, props.conversion.targetModuleKey).catch(() => undefined);
  const known = new Set((schema?.blocks ?? []).flatMap((b) => b.fields).map((f) => f.key));
  const added = candidates.filter((k) => known.has(k) && !inputKeys.value.includes(k));
  if (added.length) extraKeys.value = [...extraKeys.value, ...added];
  await nextTick();
  await nextTick();
  const shown = new Set(inputFields.value.map((f) => f.key));
  setErrors(Object.fromEntries(Object.entries(errors).filter(([k]) => shown.has(k)).map(([k, v]) => [k, v[0]])));
}

async function submit() {
  serverErrors.value = new Map();
  const sent = lines.value ? convertLines(lines.value, quantities as Record<number, number>) : undefined;
  const filled = Object.fromEntries(inputFields.value.filter((f) => !isBlank(formValues[f.key])).map((f) => [f.key, formValues[f.key]]));
  const body: ConvertBody = { rowVersion: props.rowVersion, ...(Object.keys(filled).length && { values: filled }), ...(sent && { lines: sent }) };
  try {
    const rec = await convert.mutateAsync({ id: props.recordId, key: props.conversion.key, body, idempotencyKey });
    idempotencyKey = newIdempotencyKey();
    emit('update:modelValue', false);
    $q.notify({ type: 'positive', message: t('docflow.convert.done') });
    await router.push(`/dynamic/${rec.moduleKey}/${rec.id}`);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 422) idempotencyKey = newIdempotencyKey();
    if (err.status === 400 && err.errors) {
      serverErrors.value = serverQuantityErrors(err.errors, sent ?? []);
      await showServerFieldErrors(err.errors);
      const fieldKeys = inputFields.value.map((f) => f.key);
      const rest = messagesOutside(err.errors, [...fieldKeys, ...Object.keys(err.errors).filter((k) => QUANTITY_KEY.test(k))]);
      if (rest.length) $q.notify({ type: 'negative', message: rest.join('、') });
    } else if (err.status === 409) {
      $q.notify({
        type: 'warning', message: t('docflow.convert.stale'),
        actions: [{ label: t('dynamic.reload'), color: 'white', handler: () => void reload() }],
      });
    } else $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
  }
}
</script>

<template>
  <q-dialog :model-value="modelValue" persistent @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="min-width: 640px; max-width: 95vw" data-testid="convert-dialog">
      <q-card-section class="text-h6">{{ t('docflow.convert.title', { name: resolveI18n(conversion.targetName, locale) }) }}</q-card-section>
      <q-card-section v-if="loadFailed" class="text-negative" data-testid="convert-inputs-failed">{{ t('docflow.convert.inputsFailed') }}</q-card-section>
      <q-card-section v-else-if="!inputsReady" class="text-muted" data-testid="convert-inputs-loading">{{ t('docflow.convert.loadingInputs') }}</q-card-section>
      <q-card-section v-else-if="inputFields.length" data-testid="convert-inputs">
        <div class="q-mb-sm">{{ t('docflow.convert.fillRequired') }}</div>
        <div class="column q-gutter-y-sm">
          <div v-for="f in inputFields" :key="f.key" :data-testid="`convert-input-${f.key}`">
            <DynamicField :field="f" :required="requiredKeys.has(f.key)" />
          </div>
        </div>
      </q-card-section>
      <q-card-section v-if="lines">
        <div v-if="!lines.length" class="text-muted">{{ t('docflow.convert.noLines') }}</div>
        <q-markup-table v-else flat bordered dense>
          <thead>
            <tr>
              <th class="text-left">{{ t('docflow.convert.line') }}</th>
              <th class="text-right">{{ t('docflow.convert.quantity') }}</th>
              <th class="text-right">{{ t('docflow.convert.converted') }}</th>
              <th class="text-right">{{ t('docflow.convert.remaining') }}</th>
              <th class="text-right" style="width: 160px">{{ t('docflow.convert.thisTime') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="l in lines" :key="l.lineId" :data-testid="`convert-line-${l.lineId}`">
              <td class="text-left">{{ l.title }}</td>
              <td class="text-right">{{ l.quantity }}</td>
              <td class="text-right">{{ l.converted }}</td>
              <td class="text-right" :class="{ 'text-negative': l.remaining < 0 }">{{ l.remaining }}</td>
              <td class="text-right">
                <q-input
                  :model-value="quantities[l.lineId]" type="number" min="0" :max="Math.max(0, l.remaining)" step="any" dense outlined
                  :disable="l.remaining <= 0" :error="!!errorText(l.lineId)" :error-message="errorText(l.lineId)"
                  :data-testid="`convert-quantity-${l.lineId}`" @update:model-value="(v) => setQuantity(l.lineId, v)"
                />
              </td>
            </tr>
          </tbody>
        </q-markup-table>
        <div class="text-caption text-muted q-mt-sm">{{ t('docflow.convert.hint') }}</div>
      </q-card-section>
      <q-card-section v-else>{{ t('docflow.convert.headerOnly', { name: resolveI18n(conversion.targetName, locale) }) }}</q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.cancel')" data-testid="convert-cancel" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" :label="t('docflow.convert.submit')" :disable="!valid" :loading="convert.isPending.value" data-testid="convert-submit" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
