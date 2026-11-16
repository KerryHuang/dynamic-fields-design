<script setup lang="ts">
import { computed } from 'vue';
import { useQuasar } from 'quasar';
import { useI18n } from 'vue-i18n';
import type { ApprovalNodeDef } from 'src/domain/dynamic/approval-flow-types';
import { setNode } from 'src/domain/dynamic/approval-graph-edit';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { nodeLabel } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 分支節點：名稱，分支清單唯讀。新增分支要從分支節點再拉一條線；手機不能拉線，所以不顯示這句。
const props = defineProps<{ designer: GraphDesigner; node: ApprovalNodeDef }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const def = computed(() => props.designer.state.value!.definition);
const branches = computed(() => def.value.edges.filter((e) => e.from === props.node.id).map((e) => {
  const n = def.value.nodes.find((x) => x.id === e.to);
  return { id: e.id, name: n ? nodeLabel(n, locale.value, t) : e.to };
}));
const setName = (name: Record<string, string>) => props.designer.edit((s) => setNode(s, props.node.id, { name: Object.keys(name).length ? name : undefined }), `node:${props.node.id}:name`);
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="fork-node-panel">
    <I18nTextInput :model-value="node.name ?? {}" :label="t('approval.designer.stage.name')" optional @update:model-value="setName" />
    <div>
      <div class="text-subtitle2">{{ t('approval.designer.graph.forkBranches') }}</div>
      <q-list dense>
        <q-item v-for="b in branches" :key="b.id" :data-testid="`fork-branch-${b.id}`"><q-item-section>{{ b.name }}</q-item-section></q-item>
      </q-list>
      <div v-if="!$q.screen.lt.sm" class="text-caption text-muted" data-testid="fork-hint">{{ t('approval.designer.graph.forkHint') }}</div>
    </div>
  </div>
</template>
