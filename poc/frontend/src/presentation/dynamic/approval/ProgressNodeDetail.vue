<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalActionView, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import ApprovalActionLog from './ApprovalActionLog.vue';
import ApprovalStageList from './ApprovalStageList.vue';

// 進度圖節點的細節（U6）：該節點每一次執行與那幾次的歷程，原樣重用時間軸元件，所以順序、內容與時間軸一致（舊的在上）。
// 資料就是時間軸那一份（`stages`、`actions`），這裡只依 `runIds` 篩；讀者看不到的段後端本來就沒給（`changed` 的 `runIds` 已只留看得到的）。
const props = defineProps<{
  modelValue: boolean; name: string; state: string; runIds: number[]; stages: ApprovalStageView[]; actions: ApprovalActionView[]; schema: ModuleSchema | null;
  /** 手機（全螢幕檢視裡）改成底部抽屜。 */
  bottom?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t } = useI18n();

const runs = computed(() => new Set(props.runIds));
const nodeStages = computed(() => props.stages.filter((s) => runs.value.has(s.id)));
const nodeActions = computed(() => props.actions.filter((a) => a.stageRunId != null && runs.value.has(a.stageRunId)));
const title = computed(() => (props.state ? t('approval.graph.detailTitle', { name: props.name, state: props.state }) : props.name));
</script>

<template>
  <q-dialog :model-value="modelValue" :position="bottom ? 'bottom' : 'standard'" @update:model-value="emit('update:modelValue', $event)">
    <q-card class="pg-detail" :class="{ 'pg-detail-bottom': bottom }" role="dialog" :aria-label="title" data-testid="pg-detail">
      <q-card-section class="row items-center no-wrap q-pb-none">
        <div class="text-subtitle1 col pg-detail-title" data-testid="pg-detail-title">{{ title }}</div>
        <q-btn flat round dense icon="close" :aria-label="t('approval.graph.close')" data-testid="pg-detail-close" @click="emit('update:modelValue', false)" />
      </q-card-section>
      <q-card-section class="pg-detail-body">
        <ApprovalStageList :stages="nodeStages" />
        <ApprovalActionLog :actions="nodeActions" :schema="schema" />
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.pg-detail { width: min(560px, 92vw); max-height: 80vh; display: flex; flex-direction: column; }
.pg-detail-bottom { width: 100%; max-width: 100%; border-radius: var(--t-radius) var(--t-radius) 0 0; }
.pg-detail-title { overflow-wrap: anywhere; }
.pg-detail-body { overflow-y: auto; min-height: 0; }
</style>
