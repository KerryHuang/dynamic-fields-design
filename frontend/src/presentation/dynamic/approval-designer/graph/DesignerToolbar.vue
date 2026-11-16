<script setup lang="ts">
import { useI18n } from 'vue-i18n';

// 設計頁工具列（§10.2）：唯讀者只有「全圖」「節點清單」（節點清單隨 Task 9 加入）；手機只留圖示。儲存、發布等寫入動作隨 Task 10 加入。
defineProps<{ readonly: boolean; phone: boolean; showFlowProps: boolean; undoable: boolean; redoable: boolean }>();
const emit = defineEmits<{ fit: []; flowProps: []; undo: []; redo: [] }>();
const { t } = useI18n();
</script>

<template>
  <div class="row items-center no-wrap q-gutter-x-xs de-toolbar" role="toolbar" :aria-label="t('approval.designer.graph.title')" data-testid="de-toolbar">
    <q-btn flat dense no-caps icon="fit_screen" :label="phone ? undefined : t('approval.designer.graph.fit')" :aria-label="t('approval.designer.graph.fit')" data-testid="tb-fit" @click="emit('fit')" />
    <q-btn
      v-if="showFlowProps" flat dense no-caps icon="tune" :label="phone ? undefined : t('approval.designer.graph.flowProps')" :aria-label="t('approval.designer.graph.flowProps')"
      data-testid="tb-flow-props" @click="emit('flowProps')"
    />
    <template v-if="!readonly">
      <q-btn flat dense round icon="undo" :disable="!undoable" :aria-label="t('approval.designer.graph.undo')" data-testid="tb-undo" @click="emit('undo')" />
      <q-btn flat dense round icon="redo" :disable="!redoable" :aria-label="t('approval.designer.graph.redo')" data-testid="tb-redo" @click="emit('redo')" />
    </template>
    <slot />
  </div>
</template>
