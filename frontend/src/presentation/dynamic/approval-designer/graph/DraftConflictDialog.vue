<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { Conflict } from './use-designer-actions';

// 草稿衝突（409 DraftConflict）：誰在什麼時候存了新的草稿；覆蓋／重新載入／取消。
const props = defineProps<{ conflict: Conflict | null; busy: boolean }>();
const emit = defineEmits<{ resolve: [choice: 'overwrite' | 'reload' | 'cancel'] }>();
const { t, locale } = useI18n();
const body = computed(() => {
  const i = props.conflict?.info;
  if (!i) return '';
  const time = i.draftUpdatedTime ? new Date(i.draftUpdatedTime).toLocaleString(locale.value) : '';
  const name = i.draftUpdatedByName ?? i.draftUpdatedBy;
  return name ? t('approval.designer.graph.conflictBody', { name, time }) : t('approval.designer.graph.conflictBodyNoName', { time });
});
</script>

<template>
  <q-dialog :model-value="conflict !== null" persistent aria-labelledby="de-conflict-title" @update:model-value="emit('resolve', 'cancel')">
    <q-card v-if="conflict" style="min-width: min(420px, 92vw)" data-testid="conflict-dialog">
      <q-card-section id="de-conflict-title" class="text-h6 q-pb-none">{{ t('approval.designer.graph.conflictTitle') }}</q-card-section>
      <q-card-section data-testid="conflict-body">{{ body }}</q-card-section>
      <q-card-actions align="right" class="q-gutter-xs">
        <q-btn flat no-caps :label="t('approval.designer.graph.cancel')" data-testid="conflict-cancel" @click="emit('resolve', 'cancel')" />
        <q-btn flat no-caps :label="t('approval.designer.graph.conflictReload')" data-testid="conflict-reload" @click="emit('resolve', 'reload')" />
        <q-btn
          color="primary" unelevated no-caps :loading="busy" :label="conflict.action === 'discard' ? t('approval.designer.graph.conflictDiscard') : t('approval.designer.graph.conflictOverwrite')"
          data-testid="conflict-overwrite" @click="emit('resolve', 'overwrite')"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
