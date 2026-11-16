<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { usePreviewApprovalDraft } from 'src/application/dynamic/queries/approval-flow-queries';
import type { ApprovalFlow, ApprovalFlowInput } from 'src/domain/dynamic/approval-flow-types';
import { mapGraphIssues } from 'src/domain/dynamic/approval-graph-errors';
import type { ApprovalPreview } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import PreviewStages from 'src/presentation/dynamic/approval/PreviewStages.vue';
import type { ApiError } from 'src/services/api/http';
import TrialInputs from '../TrialInputs.vue';
import { trialFieldIds } from '../trial-fields';
import { issueRows } from './designer-graph';
import { trialOverlay, type TrialOverlay } from './designer-trial';
import { draftPriority } from './use-designer-actions';
import type { GraphDesigner } from './use-graph-designer';

// 設計頁的試算（§10.6、J80）：送編輯中、未儲存的工作副本到 POST approval-flows/preview，結果疊在畫布上（`overlay`），同時列在這裡的清單。
// 預覽端點只要讀取權，所以唯讀的設計者也能用（U62）。`open` 變 false（結束試算）就清掉結果與疊圖；發起人、記錄、欄位值留著，再開不用重填。
const props = defineProps<{ designer: GraphDesigner; module: ModuleDetail; flows: ApprovalFlow[]; phone: boolean; open: boolean }>();
const emit = defineEmits<{ overlay: [overlay: TrialOverlay | null]; close: []; showOnCanvas: [] }>();
const { t, locale } = useI18n();

/** 試算送出的：工作副本的定義、流程名稱與目標狀態，視為啟用（後端同樣處理）；座標不進來，拖節點不會讓結果失效。沒選目標狀態就沒有。 */
const input = computed<ApprovalFlowInput | null>(() => {
  const m = props.designer.meta.value;
  const out = props.designer.output();
  if (!m || !out || m.toOptionId == null) return null;
  const s = props.designer.server.value;
  return { key: s.isNew ? m.key : s.key, name: m.name, toOptionId: m.toOptionId, priority: draftPriority(s, m.toOptionId, props.flows), active: true, definition: out.definition };
});
const fieldIds = computed(() => (input.value ? trialFieldIds(props.flows, input.value) : []));
const inputs = ref<InstanceType<typeof TrialInputs> | null>(null);
const initiator = computed(() => inputs.value?.initiator ?? null);

const preview = usePreviewApprovalDraft(() => props.module.key);
const result = ref<ApprovalPreview | null>(null);
const errors = ref<string[]>([]);
const failed = ref(false);
// 編輯讓結果過期；編輯或重跑之後才到的回應丟掉。
let runId = 0;
const reset = () => { runId++; result.value = null; errors.value = []; failed.value = false; };
watch(() => JSON.stringify(input.value), reset);
watch(() => props.open, (o) => { if (!o) reset(); });

function run() {
  const draft = input.value;
  if (!draft || !initiator.value) return;
  reset();
  const myRun = runId;
  preview.mutate({ draft, initiatorUserId: initiator.value, recordId: inputs.value?.recordId ?? undefined, values: inputs.value?.sentValues }, {
    onSuccess: (r) => { if (myRun === runId) result.value = r; },
    onError: (e) => {
      if (myRun !== runId) return;
      const err = e as unknown as ApiError;
      const rows = err.status === 400 && err.errors && draft.definition.shape === 'graph'
        ? issueRows(mapGraphIssues(err.errors, [], draft.definition), draft.definition, { locale: locale.value, t })
        : [];
      if (rows.length) errors.value = rows.map((r) => `${r.title}：${r.message}`);
      else failed.value = true;
    },
  });
}

const mine = computed(() => !!result.value && result.value.flowKey === input.value?.key);
const otherName = computed(() => {
  const r = result.value;
  if (!r || mine.value) return '';
  const saved = props.flows.find((f) => f.key === r.flowKey);
  return resolveI18n(saved?.name ?? r.flowName ?? {}, locale.value) || r.flowKey || '';
});
const overlay = computed(() => (input.value && input.value.definition.shape === 'graph' ? trialOverlay(input.value.definition, result.value, input.value.key) : null));
watch(overlay, (o) => emit('overlay', o), { immediate: true });
/** 命中了流程卻沒有疊圖（命中的是別條流程）：告訴使用者結果只列在清單。 */
const listOnly = computed(() => !!result.value && result.value.outcome === 'flow' && !overlay.value);
</script>

<template>
  <div class="column no-wrap de-trial-body" data-testid="de-trial">
    <div class="row items-center no-wrap q-pa-sm">
      <div class="col text-subtitle1 ellipsis">{{ t('approval.designer.trialPanel.title') }}</div>
      <q-btn flat dense no-caps icon="close" :label="phone ? undefined : t('approval.designer.trialPanel.close')" :aria-label="t('approval.designer.trialPanel.close')" data-testid="de-trial-close" @click="emit('close')" />
    </div>
    <q-separator />
    <div class="col scroll q-pa-sm" :style="{ minHeight: 0 }">
      <div v-if="!input" class="text-muted" data-testid="trial-need-target">{{ t('approval.designer.trial.needTarget') }}</div>
      <div v-else class="column no-wrap q-gutter-y-md">
        <TrialInputs ref="inputs" :module="module" :field-ids="fieldIds" @change="reset" />
        <div>
          <q-btn
            color="primary" unelevated no-caps :label="t('approval.designer.trial.run')" :disable="!initiator" :loading="preview.isPending.value"
            data-testid="trial-run" @click="run"
          />
        </div>

        <div v-if="errors.length" class="text-negative" data-testid="trial-errors">
          <div class="text-subtitle2">{{ t('approval.designer.trial.draftInvalid') }}</div>
          <div v-for="(m, i) in errors" :key="i">{{ m }}</div>
        </div>
        <div v-else-if="failed" class="text-negative" data-testid="trial-failed">{{ t('approval.designer.trial.failed') }}</div>
        <div v-else-if="result" data-testid="trial-result">
          <div v-if="result.outcome === 'blocked'" class="text-negative" data-testid="trial-blocked">{{ t('approval.designer.trial.blocked') }}</div>
          <div v-else-if="result.outcome === 'direct'" data-testid="trial-direct">{{ t('approval.designer.trial.direct') }}</div>
          <template v-else>
            <div v-if="mine" class="text-subtitle2 q-mb-sm" data-testid="trial-flow">{{ t('approval.designer.trial.matched', { name: resolveI18n(result.flowName ?? {}, locale) }) }}</div>
            <q-banner v-else dense rounded class="bg-info-soft q-mb-sm" data-testid="trial-other-flow">{{ t('approval.designer.trial.otherFlow', { name: otherName }) }}</q-banner>
            <div v-if="overlay" class="text-caption text-muted q-mb-sm" data-testid="trial-legend">{{ t('approval.designer.trialPanel.legend') }}</div>
            <div v-else-if="listOnly" class="text-caption text-muted q-mb-sm" data-testid="trial-list-only">{{ t('approval.designer.trialPanel.listOnly') }}</div>
            <q-btn
              v-if="phone && overlay" outline color="primary" no-caps icon="account_tree" class="q-mb-sm" :label="t('approval.designer.trialPanel.showOnCanvas')"
              data-testid="trial-show-on-canvas" @click="emit('showOnCanvas')"
            />
            <div class="text-subtitle2" data-testid="trial-list-title">{{ t('approval.designer.trialPanel.list') }}</div>
            <PreviewStages :stages="result.stages" designer />
          </template>
        </div>
        <div class="text-caption text-muted">{{ t('approval.designer.trial.note') }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.de-trial-body { height: 100%; min-height: 0; }
</style>
