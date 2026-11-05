<script setup lang="ts">
import { useQuasar } from 'quasar';
import { toRef } from 'vue';
import { useI18n } from 'vue-i18n';
import { usePreviewSchema } from 'src/application/dynamic/queries/schema-queries';
import DynamicForm from 'src/presentation/dynamic/components/DynamicForm.vue';

const props = defineProps<{ moduleKey: string; version: number }>();
const { t } = useI18n();
const $q = useQuasar();
const { data: schema } = usePreviewSchema(toRef(props, 'moduleKey'), toRef(props, 'version'));
</script>

<template>
  <div>
    <q-banner dense class="bg-blue-1 text-grey-9 q-mb-md" data-testid="preview-notice">{{ t('designer.preview.notice') }}</q-banner>
    <DynamicForm v-if="schema" :schema="schema" mode="create" @submit="$q.notify({ type: 'info', message: t('designer.preview.notSaved') })" />
  </div>
</template>
