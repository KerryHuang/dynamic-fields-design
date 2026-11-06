<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { operatorNeedsValue } from 'src/domain/dynamic/condition';
import type { CatalogField } from 'src/domain/dynamic/report-types';
import type { FilterOperator } from 'src/domain/dynamic/types';

const props = defineProps<{ field: CatalogField; op: string; modelValue: unknown }>();
const emit = defineEmits<{ 'update:modelValue': [value: unknown] }>();
const { t } = useI18n();
const users = useUsers();

const need = computed(() => operatorNeedsValue(props.op as FilterOperator));
const NUMBER_TYPES = ['integer', 'decimal', 'currency', 'percent', 'reference'];
const kind = computed<'option' | 'bool' | 'user' | 'number' | 'date' | 'datetime-local' | 'time' | 'text'>(() => {
  const f = props.field;
  if (need.value === 'days') return 'number';
  if (f.type === 'enum' || f.type === 'multienum') return 'option';
  if (f.type === 'boolean') return 'bool';
  if (f.type === 'user') return 'user';
  if (f.type === 'datetime') return 'datetime-local';
  if (f.type === 'date' || f.isDate) return 'date';
  if (f.type === 'time') return 'time';
  if (NUMBER_TYPES.includes(f.type) || f.type === 'formula' || f.type === 'rollup') return 'number';
  return 'text';
});

// 停用的選項只能取消，不能新選；已存的值仍要顯示得出來
const optionChoices = computed(() => {
  if (kind.value === 'option')
    return (props.field.options ?? []).map((o) => ({ value: o.id, label: o.active ? o.label : `${o.label}${t('reports.inactiveMark')}`, disable: !o.active }));
  if (kind.value === 'user') return (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName }));
  return [{ value: true, label: t('reports.designer.yes') }, { value: false, label: t('reports.designer.no') }];
});
const inputType = computed(() => (['number', 'date', 'datetime-local', 'time'].includes(kind.value) ? (kind.value as 'number' | 'date' | 'datetime-local' | 'time') : 'text'));
const isChoice = computed(() => ['option', 'bool', 'user'].includes(kind.value));

const scalar = (raw: unknown) => (kind.value === 'number' ? (raw === '' || raw === null ? null : Number(raw)) : raw);
const listText = computed(() => (Array.isArray(props.modelValue) ? props.modelValue.join(', ') : ''));
function onListText(raw: string | number | null) {
  emit('update:modelValue', String(raw ?? '').split(/[,\n，]/).map((x) => x.trim()).filter((x) => x !== '').map(scalar));
}
const pair = computed(() => (Array.isArray(props.modelValue) ? props.modelValue : [null, null]));
const setPair = (i: 0 | 1, v: unknown) => emit('update:modelValue', i === 0 ? [scalar(v), pair.value[1]] : [pair.value[0], scalar(v)]);
</script>

<template>
  <div v-if="need !== 'none'" class="row items-center q-gutter-x-sm no-wrap" data-testid="filter-value">
    <template v-if="need === 'pair'">
      <q-select
        v-if="isChoice" dense outlined emit-value map-options :options="optionChoices" :model-value="pair[0]" class="col"
        @update:model-value="setPair(0, $event)"
      />
      <q-input v-else dense outlined :type="inputType" :model-value="pair[0] ?? ''" class="col" @update:model-value="setPair(0, $event)" />
      <span>{{ t('dynamic.filter.to') }}</span>
      <q-select
        v-if="isChoice" dense outlined emit-value map-options :options="optionChoices" :model-value="pair[1]" class="col"
        @update:model-value="setPair(1, $event)"
      />
      <q-input v-else dense outlined :type="inputType" :model-value="pair[1] ?? ''" class="col" @update:model-value="setPair(1, $event)" />
    </template>
    <template v-else-if="need === 'list'">
      <q-select
        v-if="isChoice" dense outlined multiple use-chips emit-value map-options :options="optionChoices"
        :model-value="Array.isArray(modelValue) ? modelValue : []" class="col" @update:model-value="emit('update:modelValue', $event)"
      />
      <q-input
        v-else dense outlined :model-value="listText" class="col" :hint="t('dynamic.filter.listHint')" @update:model-value="onListText"
      />
    </template>
    <q-select
      v-else-if="isChoice" dense outlined emit-value map-options :options="optionChoices" :model-value="modelValue" class="col"
      @update:model-value="emit('update:modelValue', $event)"
    />
    <q-input
      v-else dense outlined :type="inputType" :model-value="(modelValue as string | number | null | undefined) ?? ''" class="col"
      @update:model-value="emit('update:modelValue', scalar($event))"
    />
  </div>
</template>
