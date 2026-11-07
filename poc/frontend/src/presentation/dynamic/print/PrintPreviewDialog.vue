<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';

// The document comes from the server already sanitized and with every value escaped. The sandbox is the second
// layer: no `allow-scripts`, so even a markup bug could not run code; `allow-same-origin` lets us call print() on
// the frame, and `allow-modals` lets the browser show its print dialog.
defineProps<{ modelValue: boolean; html: string; title: string }>();
defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t } = useI18n();
const frame = ref<HTMLIFrameElement>();

const print = () => frame.value?.contentWindow?.print();
</script>

<template>
  <q-dialog :model-value="modelValue" maximized @update:model-value="(v) => $emit('update:modelValue', v)">
    <q-card class="column no-wrap" data-testid="print-preview">
      <q-card-section class="row items-center q-gutter-sm">
        <div class="text-h6">{{ title }}</div>
        <q-space />
        <q-btn color="primary" icon="print" :label="t('print.print')" data-testid="print-now" @click="print" />
        <q-btn flat :label="t('dynamic.cancel')" v-close-popup />
      </q-card-section>
      <q-separator />
      <iframe
        ref="frame" :srcdoc="html" sandbox="allow-same-origin allow-modals" referrerpolicy="no-referrer"
        class="col" style="border: 0; width: 100%" :title="title" data-testid="print-frame"
      />
    </q-card>
  </q-dialog>
</template>
