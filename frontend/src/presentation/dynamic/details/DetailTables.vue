<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import InlineEditableDataTable from 'src/components/framework/InlineEditableDataTable.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DynamicRecord, SchemaDetail } from 'src/domain/dynamic/types';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import { referenceView } from 'src/presentation/dynamic/reference-display';

defineProps<{ details: SchemaDetail[]; rows: Record<string, DynamicRecord[]> }>();
const { locale, t } = useI18n();

const fieldsOf = (d: SchemaDetail) => d.fields.filter((f) => f.key !== d.parentFieldKey);
const columnsOf = (d: SchemaDetail) => fieldsOf(d).map((f) => ({ name: f.key, label: resolveI18n(f.label, locale.value) }));
</script>

<template>
  <div class="q-gutter-md q-mt-none">
    <div v-for="d in details" :key="d.moduleKey" :data-testid="`detail-${d.moduleKey}`">
      <div class="text-subtitle1 q-mb-sm">{{ resolveI18n(d.name, locale) }}</div>
      <InlineEditableDataTable :columns="columnsOf(d)" :rows="rows[d.moduleKey] ?? []" :row-key="(r) => String(r.id)" readonly>
        <template v-for="f in fieldsOf(d)" :key="f.key" #[`cell-${f.key}`]="{ row }">
          <template v-if="f.type === 'reference'">
            <template v-if="referenceView(row, f, t)">
              <router-link v-if="referenceView(row, f, t)!.to" :to="referenceView(row, f, t)!.to!">{{ referenceView(row, f, t)!.text }}</router-link>
              <template v-else>{{ referenceView(row, f, t)!.text }}</template>
            </template>
          </template>
          <template v-else>{{ fieldTypeRegistry[f.type].format(row.values[f.key], f, locale) }}</template>
        </template>
      </InlineEditableDataTable>
    </div>
  </div>
</template>
