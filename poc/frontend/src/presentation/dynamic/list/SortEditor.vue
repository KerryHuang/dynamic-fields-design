<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleSchema, ViewSort } from 'src/domain/dynamic/types';

const MAX_SORTS = 3;
const props = defineProps<{ schema: ModuleSchema; modelValue: ViewSort[] }>();
const emit = defineEmits<{ 'update:modelValue': [value: ViewSort[]] }>();
const { t, locale } = useI18n();

const sortable = computed(() => props.schema.blocks.flatMap((b) => b.fields).filter((f) => f.config.isSortable));
const label = (id: number) => resolveI18n(sortable.value.find((f) => f.id === id)?.label, locale.value);
const optionsFor = (index: number) => {
  const taken = new Set(props.modelValue.filter((_, i) => i !== index).map((s) => s.field));
  return sortable.value.filter((f) => !taken.has(f.id)).map((f) => ({ value: String(f.id), label: label(f.id) }));
};
const orders = computed(() => [
  { value: 'asc', label: t('dynamic.sort.asc') },
  { value: 'desc', label: t('dynamic.sort.desc') },
]);
const canAdd = computed(() => props.modelValue.length < MAX_SORTS && props.modelValue.length < sortable.value.length);

function patch(index: number, change: Partial<ViewSort>) {
  emit('update:modelValue', props.modelValue.map((s, i) => (i === index ? { ...s, ...change } : s)));
}
function add() {
  const taken = new Set(props.modelValue.map((s) => s.field));
  const next = sortable.value.find((f) => !taken.has(f.id));
  if (next && canAdd.value) emit('update:modelValue', [...props.modelValue, { field: next.id, order: 'asc' }]);
}
</script>

<template>
  <div class="column q-gutter-sm">
    <div v-for="(s, i) in modelValue" :key="i" class="row q-gutter-sm items-center no-wrap">
      <GenericSelect
        :model-value="String(s.field)" :label="t('dynamic.sort.field')" :options="optionsFor(i)" class="col"
        @update:model-value="(v) => v && patch(i, { field: Number(v) })"
      />
      <GenericSelect
        :model-value="s.order" :label="t('dynamic.sort.order')" :options="orders" required style="min-width: 8rem"
        @update:model-value="(v) => patch(i, { order: v === 'desc' ? 'desc' : 'asc' })"
      />
      <q-btn flat round dense icon="close" :aria-label="t('dynamic.sort.remove')" @click="emit('update:modelValue', modelValue.filter((_, j) => j !== i))" />
    </div>
    <div><q-btn flat icon="add" :label="t('dynamic.sort.add')" :disable="!canAdd" data-testid="add-sort" @click="add" /></div>
  </div>
</template>
