<script setup lang="ts">
import { useQueries } from '@tanstack/vue-query';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import { operatorNeedsValue } from 'src/domain/dynamic/condition';
import type { CatalogField } from 'src/domain/dynamic/report-types';
import type { FilterOperator } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

const props = defineProps<{ field: CatalogField; op: string; modelValue: unknown }>();
const emit = defineEmits<{ 'update:modelValue': [value: unknown] }>();
const { t } = useI18n();
const users = useUsers();

const need = computed(() => operatorNeedsValue(props.op as FilterOperator));
const NUMBER_TYPES = ['integer', 'decimal', 'currency', 'percent'];
const kind = computed<'reference' | 'option' | 'bool' | 'user' | 'number' | 'date' | 'datetime-local' | 'time' | 'text'>(() => {
  const f = props.field;
  if (need.value === 'days') return 'number';
  if (f.type === 'reference' && f.refKey) return 'reference';
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

// 參照欄位：重用列表篩選的記錄挑選器；模組目標的值是記錄 id，標題逐筆查（沒有 Read 範圍或已刪除就標出來），實體目標的鍵本身就是可讀代碼
const lightUrl = computed(() => (props.field.refKind === 'entity' ? `/entities/${props.field.refKey}/light` : `/modules/${props.field.refKey}/records/light`));
const refValues = computed(() => (kind.value === 'reference' && props.field.refKind !== 'entity'
  ? [props.modelValue].flat().filter((v): v is string | number => v !== null && v !== undefined && v !== '') : []));
const recordTitles = useQueries({
  queries: computed(() => refValues.value.map((id) => ({
    queryKey: ['record-title', id], queryFn: () => dynamicApi.getRecord(Number(id)), retry: false, staleTime: 30_000,
  }))),
});
const knownTitles = computed(() => Object.fromEntries(refValues.value.map((id, i) => {
  const r = recordTitles.value[i];
  const status = (r?.error as { status?: number } | null | undefined)?.status;
  return [String(id), r?.data ? r.data.title : status === 404 ? t('dynamic.reference.missing') : status === 403 ? t('dynamic.reference.restricted') : String(id)];
})));

// datetime-local 只認 yyyy-MM-ddTHH:mm 的台北牆上時間；存檔後的值是 yyyy-MM-ddTHH:mm:ss+08:00，其他偏移換算成台北時間
function toLocalInput(v: unknown): string {
  if (typeof v !== 'string') return '';
  if (/^\d{4}-\d{2}-\d{2}$/.test(v)) return `${v}T00:00`;
  if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(v)) return v.slice(0, 16); // 沒有偏移：台北牆上時間
  if (v.endsWith('+08:00')) return v.slice(0, 16);
  const ms = Date.parse(v);
  return Number.isNaN(ms) ? '' : new Date(ms + 8 * 3_600_000).toISOString().slice(0, 16);
}
const shown = (v: unknown) => (kind.value === 'datetime-local' ? toLocalInput(v) : ((v as string | number | null | undefined) ?? ''));
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
    <div v-if="kind === 'reference'" class="col">
      <PopupSelect
        :model-value="(modelValue as string | number | (string | number)[] | null | undefined)" :light-url="lightUrl"
        :value-kind="field.refKind === 'entity' ? 'string' : 'number'" :multiple="need === 'list'" :known-titles="knownTitles"
        @update:model-value="emit('update:modelValue', $event)"
      />
    </div>
    <template v-else-if="need === 'pair'">
      <q-select
        v-if="isChoice" dense outlined emit-value map-options :options="optionChoices" :model-value="pair[0]" class="col"
        @update:model-value="setPair(0, $event)"
      />
      <q-input v-else dense outlined :type="inputType" :model-value="shown(pair[0])" class="col" @update:model-value="setPair(0, $event)" />
      <span>{{ t('dynamic.filter.to') }}</span>
      <q-select
        v-if="isChoice" dense outlined emit-value map-options :options="optionChoices" :model-value="pair[1]" class="col"
        @update:model-value="setPair(1, $event)"
      />
      <q-input v-else dense outlined :type="inputType" :model-value="shown(pair[1])" class="col" @update:model-value="setPair(1, $event)" />
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
      v-else dense outlined :type="inputType" :model-value="shown(modelValue)" class="col"
      @update:model-value="emit('update:modelValue', scalar($event))"
    />
  </div>
</template>
