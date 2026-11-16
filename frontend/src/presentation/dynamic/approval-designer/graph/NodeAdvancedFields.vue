<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalNodeDef } from 'src/domain/dynamic/approval-flow-types';
import { renameCode } from 'src/domain/dynamic/approval-graph-edit';
import type { GraphDesigner } from './use-graph-designer';

// 進階：節點代號。已發布過的代號改掉，後端會把它當成新節點（G13 以代號對應），所以先警告（J72）。
const props = defineProps<{ designer: GraphDesigner; node: ApprovalNodeDef }>();
const { t } = useI18n();
const text = ref(props.node.id);
watch(() => props.node.id, (id) => { text.value = id; });
const published = computed(() => props.designer.server.value.reserved.includes(props.node.id));
const warn = computed(() => text.value !== props.node.id && published.value);
// 不合法（格式不符、已被別的節點用）時不套用，欄位標紅；renameCode 對這些情況回原物件
const invalid = computed(() => text.value !== props.node.id && !!props.designer.state.value && renameCode(props.designer.state.value, props.node.id, text.value) === props.designer.state.value);
function apply() {
  if (text.value === props.node.id || invalid.value) return;
  const from = props.node.id;
  props.designer.edit((s) => renameCode(s, from, text.value));
  props.designer.select({ kind: 'node', id: text.value });
}
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" data-testid="node-advanced">
    <q-expansion-item dense :label="t('approval.designer.graph.advanced')">
      <q-input
        v-model="text" :label="t('approval.designer.graph.nodeCode')" dense outlined :disable="!designer.canEdit.value" :error="invalid || undefined" :error-message="t('approval.designer.graph.codeInvalid')"
        data-testid="node-code" @change="apply" @keydown.enter="apply"
      />
      <div v-if="warn" class="text-caption text-warning" data-testid="node-code-warning">{{ t('approval.designer.graph.codeChanged') }}</div>
    </q-expansion-item>
  </div>
</template>
