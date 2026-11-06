<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useConvertRecord } from 'src/application/dynamic/queries/conversions-queries';
import { canConvert, convertLines, defaultQuantities, quantityErrors, serverQuantityErrors } from 'src/domain/dynamic/conversion';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConvertBody, RecordConversion } from 'src/domain/dynamic/types';
import { messagesOutside } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ modelValue: boolean; recordId: number; rowVersion: string; conversion: RecordConversion }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const router = useRouter();
const qc = useQueryClient();
const convert = useConvertRecord();

const lines = computed(() => props.conversion.lines);
const quantities = reactive<Record<number, number | null>>({});
const serverErrors = ref(new Map<number, string>());
watch(() => [props.modelValue, props.conversion] as const, ([open]) => {
  if (!open) return;
  serverErrors.value = new Map();
  for (const k of Object.keys(quantities)) delete quantities[Number(k)];
  Object.assign(quantities, defaultQuantities(lines.value ?? []));
}, { immediate: true });

const errors = computed(() => quantityErrors(lines.value ?? [], quantities));
const errorText = (lineId: number) => {
  const e = errors.value.get(lineId);
  return e ? t(`docflow.convert.error.${e}`) : serverErrors.value.get(lineId);
};
// A header conversion has no lines to check; a line conversion needs at least one line above 0.
const valid = computed(() => (lines.value ? canConvert(lines.value, quantities) : true));

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

async function submit() {
  serverErrors.value = new Map();
  const sent = lines.value ? convertLines(lines.value, quantities as Record<number, number>) : undefined;
  const body: ConvertBody = { rowVersion: props.rowVersion, ...(sent && { lines: sent }) };
  try {
    const rec = await convert.mutateAsync({ id: props.recordId, key: props.conversion.key, body });
    emit('update:modelValue', false);
    $q.notify({ type: 'positive', message: t('docflow.convert.done') });
    await router.push(`/dynamic/${rec.moduleKey}/${rec.id}`);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 400 && err.errors) {
      serverErrors.value = serverQuantityErrors(err.errors, sent ?? []);
      const rest = messagesOutside(err.errors, Object.keys(err.errors).filter((k) => /^lines\[\d+\]\.quantity$/.test(k)));
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
