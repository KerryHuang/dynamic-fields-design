<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { usePrintTemplates } from 'src/application/dynamic/queries/print-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import PrintPreviewDialog from './PrintPreviewDialog.vue';

const props = defineProps<{ moduleKey: string; recordId: number; grant?: boolean }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const templates = usePrintTemplates(() => props.moduleKey, () => (props.grant ? props.recordId : undefined));
const options = computed(() => templates.data.value ?? []);

const html = ref('');
const title = ref('');
const open = ref(false);
const busy = ref(false);

async function show(key: string, name: string) {
  busy.value = true;
  try {
    html.value = await dynamicApi.printRecord(props.recordId, key, locale.value);
    title.value = name;
    open.value = true;
  } catch (e) {
    $q.notify({ type: 'negative', message: (e as ApiError).message || t('print.failed') });
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <template v-if="options.length">
    <q-btn-dropdown outline icon="print" :label="t('print.print')" :loading="busy" data-testid="print-menu">
      <q-list>
        <q-item v-for="o in options" :key="o.key" v-close-popup clickable :data-testid="`print-${o.key}`" @click="show(o.key, resolveI18n(o.name, locale))">
          <q-item-section>{{ resolveI18n(o.name, locale) }}</q-item-section>
        </q-item>
      </q-list>
    </q-btn-dropdown>
    <PrintPreviewDialog v-model="open" :html="html" :title="title" />
  </template>
</template>
