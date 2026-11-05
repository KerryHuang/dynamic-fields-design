<script setup lang="ts" generic="T">
import { useI18n } from 'vue-i18n';

// POC thin wrapper; replaced by the framework component of the same name when merged.
defineProps<{ columns: { name: string; label: string }[]; rows: T[]; rowKey: (row: T) => string; readonly?: boolean }>();
defineEmits<{ add: [] }>();
defineSlots<Record<`cell-${string}`, (p: { row: T; index: number }) => unknown> & { 'row-actions'?: (p: { row: T; index: number }) => unknown }>();
const { t } = useI18n();
</script>

<template>
  <div>
    <q-markup-table flat bordered dense separator="cell" wrap-cells>
      <thead>
        <tr>
          <th v-for="c in columns" :key="c.name" class="text-left">{{ c.label }}</th>
          <th v-if="$slots['row-actions']" />
        </tr>
      </thead>
      <tbody>
        <tr v-for="(row, index) in rows" :key="rowKey(row)">
          <td v-for="c in columns" :key="c.name"><slot :name="`cell-${c.name}`" :row="row" :index="index" /></td>
          <td v-if="$slots['row-actions']"><slot name="row-actions" :row="row" :index="index" /></td>
        </tr>
        <tr v-if="!rows.length">
          <td :colspan="columns.length + 1" class="text-grey">{{ t('dynamic.noData') }}</td>
        </tr>
      </tbody>
    </q-markup-table>
    <q-btn v-if="!readonly" flat dense color="primary" icon="add" :label="t('dynamic.details.addRow')" data-testid="add-row" class="q-mt-xs" @click="$emit('add')" />
  </div>
</template>
