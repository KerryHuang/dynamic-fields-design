<script setup lang="ts">
import { useQuasar } from 'quasar';

defineProps<{ modelValue: boolean; title: string }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const $q = useQuasar();
</script>

<template>
  <!-- Full screen on a phone; elsewhere a card that stays inside the viewport. The middle part scrolls, the title and the buttons stay. -->
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="designer-dialog column no-wrap">
      <q-card-section class="text-h6">{{ title }}</q-card-section>
      <q-card-section class="col scroll column q-gutter-y-md designer-dialog__body"><slot /></q-card-section>
      <q-card-actions align="right"><slot name="actions" /></q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.designer-dialog { width: 820px; max-width: 95vw; max-height: 90vh; }
.designer-dialog__body { min-width: 0; }
</style>
