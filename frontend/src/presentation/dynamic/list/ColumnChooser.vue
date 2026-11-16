<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleSchema } from 'src/domain/dynamic/types';

const props = defineProps<{ schema: ModuleSchema; modelValue: number[] }>();
const emit = defineEmits<{ 'update:modelValue': [value: number[]] }>();
const { t, locale } = useI18n();

/** Title field and modified time are always shown, so they are not listed. */
const candidates = computed(() =>
  props.schema.blocks.flatMap((b) => b.fields).filter((f) => f.key !== props.schema.titleFieldKey));
const label = (id: number) => resolveI18n(candidates.value.find((f) => f.id === id)?.label, locale.value);
const selected = computed(() => props.modelValue.filter((id) => candidates.value.some((f) => f.id === id)));
const unselected = computed(() => candidates.value.filter((f) => !props.modelValue.includes(f.id)).map((f) => f.id));

function toggle(id: number, on: boolean) {
  if (on) emit('update:modelValue', [...props.modelValue, id]);
  else if (selected.value.length > 1) emit('update:modelValue', props.modelValue.filter((x) => x !== id));
}
/** `index` is a position in the visible list; ids outside the schema keep their slots. */
function move(index: number, delta: number) {
  const order = [...selected.value];
  const [item] = order.splice(index, 1);
  order.splice(index + delta, 0, item!);
  const queue = [...order];
  emit('update:modelValue', props.modelValue.map((id) => (candidates.value.some((f) => f.id === id) ? queue.shift()! : id)));
}
</script>

<template>
  <q-list dense>
    <q-item v-for="(id, i) in selected" :key="id">
      <q-item-section side>
        <q-checkbox :model-value="true" :disable="selected.length <= 1" @update:model-value="toggle(id, false)" />
      </q-item-section>
      <q-item-section>{{ label(id) }}</q-item-section>
      <q-item-section side class="row no-wrap">
        <q-btn flat round dense icon="arrow_upward" :disable="i === 0" :aria-label="t('dynamic.columns.moveUp')" @click="move(i, -1)" />
        <q-btn flat round dense icon="arrow_downward" :disable="i === selected.length - 1" :aria-label="t('dynamic.columns.moveDown')" @click="move(i, 1)" />
      </q-item-section>
    </q-item>
    <q-item v-for="id in unselected" :key="id">
      <q-item-section side><q-checkbox :model-value="false" @update:model-value="toggle(id, true)" /></q-item-section>
      <q-item-section>{{ label(id) }}</q-item-section>
    </q-item>
  </q-list>
</template>
