<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { nodeLabel, type Selection } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 屬性面板的外殼：依選取切換內容（Task 8 起放各種面板）。桌機與筆電是右側固定面板、平板是抽屜、手機是全螢幕對話框（§10.5），外殼由設計頁決定。
const props = defineProps<{ designer: GraphDesigner; selection: Selection; closable: boolean }>();
const emit = defineEmits<{ close: [] }>();
const { t, locale } = useI18n();
const title = computed(() => {
  const s = props.selection;
  const g = props.designer.state.value;
  if (s.kind === 'node') {
    const n = g?.definition.nodes.find((x) => x.id === s.id);
    return n ? nodeLabel(n, locale.value, t) : '';
  }
  return t('approval.designer.graph.flowProps');
});
</script>

<template>
  <div class="column no-wrap de-panel-body" data-testid="de-panel">
    <div class="row items-center no-wrap q-pa-sm">
      <div class="col text-subtitle1 ellipsis" data-testid="de-panel-title">{{ title }}</div>
      <q-btn v-if="closable" flat dense round icon="close" :aria-label="t('approval.designer.graph.close')" data-testid="de-panel-close" @click="emit('close')" />
    </div>
    <q-separator />
    <div class="col scroll q-pa-sm"><slot /></div>
  </div>
</template>
