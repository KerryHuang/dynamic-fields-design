<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ModuleCatalog } from 'src/domain/dynamic/report-types';
import { listFields, refKey, type FieldSection } from '../report-catalog';
import type { DesignerState, Unavailable } from './designer-state';

const props = defineProps<{ state: DesignerState; catalog: ModuleCatalog; unavailable: Unavailable }>();
const { t } = useI18n();
const s = props.state;

const entries = computed(() => listFields(props.catalog, s.child, t('reports.titleField')));
const sections = computed(() => (['main', 'via', 'child'] as FieldSection[])
  .map((k) => ({ key: k, entries: entries.value.filter((e) => e.section === k) }))
  .filter((x) => x.entries.length > 0));
const selected = computed(() => new Set(s.columns.map(refKey)));
function toggle(e: (typeof entries.value)[number], on: boolean) {
  const k = refKey(e.ref);
  s.columns = on ? [...s.columns, e.ref] : s.columns.filter((c) => refKey(c) !== k);
}
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="designer-step-1">
    <div v-for="sec in sections" :key="sec.key" :data-testid="`fields-${sec.key}`">
      <div class="text-subtitle2">{{ t(`reports.designer.section.${sec.key}`) }}</div>
      <div class="column">
        <q-checkbox
          v-for="e in sec.entries" :key="refKey(e.ref)" dense :label="e.label" :model-value="selected.has(refKey(e.ref))"
          :data-testid="`field-${refKey(e.ref)}`" @update:model-value="toggle(e, $event)"
        />
      </div>
    </div>
    <div v-if="unavailable.columns.length" data-testid="columns-unavailable">
      <div class="text-subtitle2 text-negative">{{ t('reports.designer.unavailableTitle') }}</div>
      <div v-for="i in unavailable.columns" :key="i" class="row items-center">
        <span class="text-negative">{{ t('reports.fieldNo', { id: s.columns[i]!.fieldId }) }}{{ t('reports.unavailableMark') }}</span>
        <q-btn flat dense no-caps color="primary" :label="t('reports.designer.remove')" :data-testid="`column-remove-${i}`" @click="s.columns.splice(i, 1)" />
      </div>
    </div>
  </div>
</template>
