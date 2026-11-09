<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { AttachmentFile, DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import AttachmentInput from 'src/components/framework/AttachmentInput.vue';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import { fieldColClass, isFullWidth } from 'src/presentation/dynamic/form-layout';
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
/** Names of the closed apps whose (frozen) fields this block shows. */
const blockLocks = (fields: SchemaField[]) => [...new Set(fields.map((f) => f.lockedBy ?? props.record.lockedBy?.[f.key] ?? null)
  .filter((l) => l !== null).map((l) => resolveI18n(l.appName, locale.value)))];
</script>

<template>
  <div class="q-gutter-md">
    <q-card v-for="block in visibleBlocks" :key="block.id" flat bordered>
      <q-card-section class="text-subtitle1">
        {{ resolveI18n(block.label, locale) }}
        <div v-for="app in blockLocks(block.fields)" :key="app" class="text-warning text-caption" data-testid="app-closed-lock">
          {{ t('dynamic.appClosedLock', { app }) }}
        </div>
      </q-card-section>
      <q-separator />
      <q-list class="row">
        <q-item v-for="f in block.fields" :key="f.key" :class="fieldColClass(block.columns, isFullWidth(f))">
          <q-item-section side class="text-muted" style="min-width: 10rem">{{ resolveI18n(f.label, locale) }}</q-item-section>
          <q-item-section :data-testid="f.key">
            <template v-if="refOf(f)">
              <router-link v-if="refOf(f)!.to" :to="refOf(f)!.to!">{{ refOf(f)!.text }}</router-link>
              <template v-else>{{ refOf(f)!.text }}</template>
            </template>
            <AttachmentInput
              v-else-if="f.type === 'attachment'" :model-value="(record.values[f.key] as AttachmentFile[] | undefined)"
              :label="resolveI18n(f.label, locale)" :field-id="f.id" :record-id="record.id" readonly
            />
            <template v-else>{{ fieldTypeRegistry[f.type].format(record.values[f.key], f, locale) }}</template>
          </q-item-section>
        </q-item>
      </q-list>
    </q-card>
  </div>
</template>
