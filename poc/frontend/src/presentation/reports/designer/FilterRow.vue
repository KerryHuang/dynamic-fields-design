<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ReportFilter } from 'src/domain/dynamic/report-types';
import type { FilterOperator } from 'src/domain/dynamic/types';
import { findEntry, type FieldEntry } from '../report-catalog';
import { operatorsOf } from './designer-state';
import FilterValueInput from './FilterValueInput.vue';

/** 單列條件編輯：條件步驟與條件占比量值共用。filter 直接修改。 */
const props = defineProps<{ filter: ReportFilter; entries: FieldEntry[]; tid: string; removable?: boolean }>();
const emit = defineEmits<{ remove: [] }>();
const { t } = useI18n();
const f = props.filter;
const entry = computed(() => findEntry(props.entries, f.field));
function setOp(op: string) {
  f.op = op;
  f.value = undefined;
}
</script>

<template>
  <div class="row items-start q-gutter-x-sm no-wrap" :data-testid="tid">
    <template v-if="entry">
      <div class="col-3 q-pt-sm">{{ entry.label }}</div>
      <q-select
        dense outlined emit-value map-options class="col-2" :model-value="f.op" :data-testid="`${tid}-op`"
        :options="operatorsOf(entry.field).map((o: FilterOperator) => ({ value: o, label: t(`dynamic.op.${o}`) }))"
        @update:model-value="setOp"
      />
      <FilterValueInput class="col" :field="entry.field" :op="f.op" :model-value="f.value" @update:model-value="f.value = $event" />
    </template>
    <div v-else class="col text-negative" :data-testid="`${tid}-unavailable`">
      {{ t('reports.fieldNo', { id: f.field.fieldId }) }}{{ t('reports.unavailableMark') }}
    </div>
    <q-btn v-if="removable !== false" flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`${tid}-remove`" @click="emit('remove')" />
  </div>
</template>
