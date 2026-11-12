<script setup lang="ts">
import { toTypedSchema } from '@vee-validate/zod';
import { useForm } from 'vee-validate';
import { computed, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import { buildZodSchema } from 'src/presentation/dynamic/build-zod-schema';
import DynamicField from 'src/presentation/dynamic/components/DynamicField.vue';
import { changedValues } from './approval-actions';

/** `keys` follow the server's order; one the reader's schema does not carry (hidden for this reader) is skipped. */
const props = defineProps<{ keys: string[]; schema: ModuleSchema; record: DynamicRecord; errors?: Record<string, string> }>();
const { t, locale } = useI18n();

const fields = computed<SchemaField[]>(() => {
  const all = new Map(props.schema.blocks.flatMap((b) => b.fields).map((f) => [f.key, f]));
  return props.keys.flatMap((k) => all.get(k) ?? []);
});
const initial = Object.fromEntries(fields.value.map((f) => [f.key, props.record.values[f.key] ?? null]));
const { values, validate, setErrors } = useForm({
  validationSchema: computed(() => ({
    ...toTypedSchema(buildZodSchema(
      { ...props.schema, blocks: [{ id: 0, label: {}, collapsed: false, columns: 1, fields: fields.value }] }, 'edit', props.record, undefined, { locale: locale.value, t },
    )),
    cast: (v: unknown) => v,
  })),
  initialValues: initial,
});
watch(() => props.errors, (errs) => {
  if (errs) setErrors(Object.fromEntries(Object.entries(errs).filter(([k]) => fields.value.some((f) => f.key === k))));
}, { deep: true });

/** Validates the editable fields; null when something is invalid, else only the values that changed. */
async function collect(): Promise<Record<string, unknown> | null> {
  const { valid } = await validate();
  return valid ? changedValues(fields.value.map((f) => f.key), initial, values as Record<string, unknown>) : null;
}
defineExpose({ collect });
</script>

<template>
  <div v-if="fields.length" class="q-mb-md" data-testid="approve-editable">
    <div class="text-subtitle2">{{ t('approval.editable.title') }}</div>
    <div class="text-caption text-muted q-mb-sm">{{ t('approval.editable.hint') }}</div>
    <div class="column no-wrap q-gutter-y-sm">
      <DynamicField v-for="f in fields" :key="f.key" :field="f" />
    </div>
  </div>
</template>
