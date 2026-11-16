<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import RowActionMenu from 'src/components/framework/RowActionMenu.vue';
import { insertStage, moveStage, removeStage, stageErrors, type StageDraft } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalStageDef } from 'src/domain/dynamic/approval-flow-types';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import ApprovalStageFields from './ApprovalStageFields.vue';
import type { FlowLimits } from './use-approval-limits';

const props = defineProps<{
  modelValue: StageDraft[];
  module: ModuleDetail;
  /** Fields a stage may make editable (from the workflow response). */
  editableFieldIds: number[];
  limits: FlowLimits;
  /** The stages as last saved: a manager level above the policy max stays selectable on a stage that already had it. */
  saved?: ApprovalStageDef[];
  errors?: Record<string, string[]>;
  disable?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: StageDraft[]] }>();
const { t } = useI18n();
const $q = useQuasar();
const mobile = computed(() => $q.screen.lt.sm);

// The backend only refuses a count above the limit that is also above the saved version's, so a flow saved with more stages may keep that many.
const atLimit = computed(() => props.limits.maxStages !== undefined && props.modelValue.length >= Math.max(props.limits.maxStages, props.saved?.length ?? 0));
// Same rule as the backend: a stage is matched to the saved one by its current code.
const savedOf = (s: StageDraft) => props.saved?.find((x) => x.code === s.code);
const codeChanged = (s: StageDraft) => !!s.savedCode && s.savedCode !== s.code && !savedOf(s);
// A code that is another saved stage's code gets that stage's relaxed limits.
const codeReused = (s: StageDraft) => s.savedCode !== s.code && !!savedOf(s);

const setAll = (draft: { stages: StageDraft[] }) => emit('update:modelValue', draft.stages);
const patch = (i: number, p: Partial<StageDraft>) => emit('update:modelValue', props.modelValue.map((s, k) => (k === i ? { ...s, ...p } : s)));
const wrap = () => ({ stages: props.modelValue });
const insertAt = (at: number) => setAll(insertStage(wrap(), at, props.saved?.map((x) => x.code)));
const move = (i: number, d: -1 | 1) => setAll(moveStage(wrap(), i, d));
const remove = (i: number) => setAll(removeStage(wrap(), i));

const errorsOf = (i: number) => stageErrors(props.errors, i);
const actionsOf = (i: number) => [
  { label: t('approval.designer.list.moveUp'), run: () => move(i, -1), disable: props.disable || i === 0, testid: `stage-up-${i}` },
  { label: t('approval.designer.list.moveDown'), run: () => move(i, 1), disable: props.disable || i === props.modelValue.length - 1, testid: `stage-down-${i}` },
  { label: t('approval.designer.stage.remove'), run: () => remove(i), disable: props.disable || props.modelValue.length <= 1, negative: true, testid: `stage-remove-${i}` },
];
</script>

<template>
  <div class="column no-wrap" data-testid="flow-stages">
    <div v-if="atLimit" class="text-caption text-warning q-mb-sm" data-testid="stages-limit">{{ t('approval.designer.stage.limitReached', { max: limits.maxStages }) }}</div>

    <template v-for="(s, i) in modelValue" :key="s.uid ?? i">
      <div class="row justify-center">
        <q-btn
          flat dense round icon="add" size="sm" :disable="disable || atLimit" :aria-label="t('approval.designer.stage.insertBefore', { n: i + 1 })"
          :data-testid="`stage-insert-${i}`" @click="insertAt(i)"
        />
      </div>
      <q-card flat bordered class="q-pa-sm" :data-testid="`stage-card-${i}`">
        <div class="row items-center no-wrap q-mb-sm">
          <div class="col text-subtitle2" style="min-width: 0">{{ t('approval.designer.stage.title', { n: i + 1 }) }}</div>
          <RowActionMenu v-if="mobile" :actions="actionsOf(i)" :testid="`stage-menu-${i}`" :aria-label="`${t('common.moreActions')} ${t('approval.designer.stage.title', { n: i + 1 })}`" />
          <div v-else class="row no-wrap items-center">
            <q-btn flat dense round icon="arrow_upward" :aria-label="`${t('approval.designer.list.moveUp')} ${t('approval.designer.stage.title', { n: i + 1 })}`" :disable="disable || i === 0" :data-testid="`stage-up-${i}`" @click="move(i, -1)" />
            <q-btn flat dense round icon="arrow_downward" :aria-label="`${t('approval.designer.list.moveDown')} ${t('approval.designer.stage.title', { n: i + 1 })}`" :disable="disable || i === modelValue.length - 1" :data-testid="`stage-down-${i}`" @click="move(i, 1)" />
            <q-btn
              flat dense round icon="delete" color="negative" :aria-label="`${t('approval.designer.stage.remove')} ${t('approval.designer.stage.title', { n: i + 1 })}`"
              :disable="disable || modelValue.length <= 1" :data-testid="`stage-remove-${i}`" @click="remove(i)"
            />
          </div>
        </div>

        <ApprovalStageFields
          :model-value="s" :module="module" :editable-field-ids="editableFieldIds" :limits="limits" :saved-approvers="savedOf(s)?.approvers" :errors="errorsOf(i)" :disable="disable"
          :idx="i" @patch="(p) => patch(i, p)"
        >
          <template #after-name>
            <q-input
              :model-value="s.code" :label="t('approval.designer.stage.code')" dense outlined :disable="disable" :data-testid="`stage-code-${i}`"
              :error="!!errorsOf(i).code || undefined" :error-message="errorsOf(i).code" @update:model-value="(v) => patch(i, { code: String(v ?? '') })"
            />
            <div v-if="codeChanged(s)" class="text-caption text-warning" :data-testid="`stage-code-warning-${i}`">{{ t('approval.designer.stage.codeChanged') }}</div>
            <div v-if="codeReused(s)" class="text-caption text-warning" :data-testid="`stage-code-reused-${i}`">{{ t('approval.designer.stage.codeReused', { code: s.code }) }}</div>
          </template>
        </ApprovalStageFields>
      </q-card>
    </template>
    <div class="row justify-center">
      <q-btn
        flat dense round icon="add" size="sm" :disable="disable || atLimit" :aria-label="t('approval.designer.stage.insertLast')"
        :data-testid="`stage-insert-${modelValue.length}`" @click="insertAt(modelValue.length)"
      />
    </div>
  </div>
</template>
