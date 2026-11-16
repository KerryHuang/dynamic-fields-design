<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { topoOrder } from 'src/domain/dynamic/approval-graph-edit';
import { nodeLabel, type Selection } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 節點清單（§10.5）：畫布的鍵盤與讀屏替代路徑。依從開始節點起的順序列出，點一項（或在項目上按 Enter）選取並開屬性；所有寬度、唯讀都有。
const props = defineProps<{ designer: GraphDesigner; selection: Selection }>();
const emit = defineEmits<{ pick: [id: string]; close: [] }>();
const { t, locale } = useI18n();
const rows = computed(() => {
  const def = props.designer.state.value?.definition;
  if (!def) return [];
  return topoOrder(def).map((id, i) => {
    const n = def.nodes.find((x) => x.id === id)!;
    const errors = props.designer.issues.value?.errors.nodes[id]?.length ?? 0;
    return { id, index: i + 1, kind: t(`approval.graph.kind.${n.kind}`), label: nodeLabel(n, locale.value, t), errors };
  });
});
</script>

<template>
  <section class="de-nodelist" role="region" :aria-label="t('approval.designer.graph.nodeList')" data-testid="de-nodelist">
    <div class="row items-center no-wrap q-px-sm">
      <div class="col text-subtitle2">{{ t('approval.designer.graph.nodeList') }}</div>
      <q-btn flat dense round size="sm" icon="close" :aria-label="t('approval.designer.graph.close')" data-testid="de-nodelist-close" @click="emit('close')" />
    </div>
    <ol class="de-nodelist-ol">
      <li v-for="r in rows" :key="r.id">
        <button
          type="button" class="de-nodelist-btn" :class="{ 'de-nodelist-sel': selection.kind === 'node' && selection.id === r.id }" :aria-current="selection.kind === 'node' && selection.id === r.id ? 'true' : undefined"
          :data-testid="`de-nodelist-${r.id}`" @click="emit('pick', r.id)"
        >
          <span class="de-nodelist-idx">{{ r.index }}</span>
          <span class="col ellipsis">{{ r.label }}</span>
          <span class="text-caption text-muted">{{ r.kind }}</span>
          <q-badge v-if="r.errors" color="negative" :label="r.errors" :aria-label="t('approval.designer.graph.errorCount', { n: r.errors })" />
        </button>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.de-nodelist { max-height: 35%; overflow: auto; border-bottom: 1px solid var(--t-border); background: var(--t-panel); }
.de-nodelist-ol { list-style: none; margin: 0; padding: 0 4px 4px; }
.de-nodelist-btn {
  display: flex; align-items: center; gap: 8px; width: 100%; min-height: 40px; padding: 4px 8px; text-align: left;
  background: transparent; border: 0; border-radius: 4px; color: inherit; cursor: pointer; font: inherit;
}
.de-nodelist-btn:hover { background: var(--t-selected-bg); }
.de-nodelist-btn:focus-visible { outline: 2px solid var(--q-primary); outline-offset: -2px; }
.de-nodelist-sel { background: var(--t-selected-bg); font-weight: 600; }
.de-nodelist-idx { flex: none; min-width: 1.5em; text-align: right; color: var(--t-muted, inherit); }
</style>
