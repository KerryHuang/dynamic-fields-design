<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import { referenceView } from 'src/presentation/dynamic/reference-display';
import { ruleStateOf } from 'src/presentation/dynamic/rule-state';

const props = defineProps<{ schema: ModuleSchema; record: DynamicRecord }>();
const { locale, t } = useI18n();
const refOf = (f: SchemaField) => (f.type === 'reference' ? referenceView(props.record, f, t) : null);

// A record is shown as stored: current and stored values are the same.
const hidden = computed(() => ruleStateOf(props.schema, props.record.values, props.record.values).hidden);
const visibleBlocks = computed(() => props.schema.blocks
  .map((b) => ({ ...b, fields: b.fields.filter((f) => !hidden.value.has(f.id)) }))
  .filter((b) => b.fields.length > 0));
</script>

<template>
  <div class="q-gutter-md">
    <q-card v-for="block in visibleBlocks" :key="block.id" flat bordered>
      <q-card-section class="text-subtitle1">{{ resolveI18n(block.label, locale) }}</q-card-section>
      <q-separator />
      <q-list>
        <q-item v-for="f in block.fields" :key="f.key">
          <q-item-section side class="text-grey-8" style="min-width: 10rem">{{ resolveI18n(f.label, locale) }}</q-item-section>
          <q-item-section :data-testid="f.key">
            <template v-if="refOf(f)">
              <router-link v-if="refOf(f)!.to" :to="refOf(f)!.to!">{{ refOf(f)!.text }}</router-link>
              <template v-else>{{ refOf(f)!.text }}</template>
            </template>
            <template v-else>{{ fieldTypeRegistry[f.type].format(record.values[f.key], f, locale) }}</template>
          </q-item-section>
        </q-item>
      </q-list>
    </q-card>
  </div>
</template>
