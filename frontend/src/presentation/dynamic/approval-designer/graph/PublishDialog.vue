<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import type { PublishInfo } from './use-designer-actions';

// 發布確認（U55）：警告與版本提示都只顯示後端乾跑回的結果，前端不自己比對版本。
defineProps<{ info: PublishInfo | null; busy: boolean }>();
const emit = defineEmits<{ confirm: []; cancel: [] }>();
const { t } = useI18n();
</script>

<template>
  <q-dialog :model-value="info !== null" persistent aria-labelledby="de-publish-title" @update:model-value="emit('cancel')">
    <q-card v-if="info" style="min-width: min(420px, 92vw)" data-testid="publish-dialog">
      <q-card-section id="de-publish-title" class="text-h6 q-pb-none">{{ t('approval.designer.graph.publishTitle') }}</q-card-section>
      <q-card-section class="column no-wrap q-gutter-y-sm">
        <div v-if="info.warnings.length" data-testid="publish-warnings">
          <div class="text-body2 q-mb-xs">{{ t('approval.designer.graph.publishWarnings') }}</div>
          <ul class="q-my-none q-pl-md"><li v-for="(w, i) in info.warnings" :key="i" class="text-warning">{{ w.message }}</li></ul>
        </div>
        <div v-if="info.newVersion && info.nextVersion != null" data-testid="publish-version">
          {{ info.pendingCount > 0 ? t('approval.designer.graph.publishNewVersion', { v: info.nextVersion, n: info.pendingCount }) : t('approval.designer.graph.publishNewVersionNone', { v: info.nextVersion }) }}
        </div>
        <div v-if="!info.warnings.length && !info.newVersion" data-testid="publish-ask">{{ t('approval.designer.graph.publishAsk') }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="t('approval.designer.graph.cancel')" data-testid="publish-cancel" @click="emit('cancel')" />
        <q-btn color="primary" unelevated no-caps :loading="busy" :label="info.warnings.length ? t('approval.designer.graph.publishAnyway') : t('approval.designer.graph.publish')" data-testid="publish-confirm" @click="emit('confirm')" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
