<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ReportFilter } from 'src/domain/dynamic/report-types';
import type { FilterOperator } from 'src/domain/dynamic/types';
import { findEntry, type FieldEntry } from '../report-catalog';
import { operatorsOf } from './designer-state';
import FilterValueInput from './FilterValueInput.vue';

/** 單列條件編輯：條件步驟與條件占比量值共用。filter 直接修改。 */
// Boolean prop 沒傳時 Vue 會當成 false，所以預設值要明講
const props = withDefaults(defineProps<{ filter: ReportFilter; entries: FieldEntry[]; tid: string; removable?: boolean }>(), { removable: true });
const emit = defineEmits<{ remove: [] }>();
const { t } = useI18n();
// 列以索引當 key，刪除中間列時元件實例會換 prop，所以一律讀 props.filter，不擷取
const entry = computed(() => findEntry(props.entries, props.filter.field));
function setOp(op: string) {
  props.filter.op = op;
  props.filter.value = undefined;
}
</script>

<template>
  <div class="row items-start q-col-gutter-x-sm" :data-testid="tid">
    <template v-if="entry">
      <div class="col-12 col-sm-3 q-pt-sm">{{ entry.label }}</div>
      <q-select
        dense outlined emit-value map-options class="col-5 col-sm-2" style="min-width: 96px" :model-value="filter.op" :data-testid="`${tid}-op`"
        :options="operatorsOf(entry.field).map((o: FilterOperator) => ({ value: o, label: t(`dynamic.op.${o}`) }))"
        @update:model-value="setOp"
      />
      <FilterValueInput class="col-7 col-sm" :field="entry.field" :op="filter.op" :model-value="filter.value" @update:model-value="filter.value = $event" />
    </template>
    <div v-else class="col text-negative" :data-testid="`${tid}-unavailable`">
      {{ t('reports.fieldNo', { id: filter.field.fieldId }) }}{{ t('reports.unavailableMark') }}
    </div>
    <q-btn v-if="removable" flat dense round icon="close" :aria-label="t('reports.designer.remove')" :data-testid="`${tid}-remove`" @click="emit('remove')" />
  </div>
</template>
