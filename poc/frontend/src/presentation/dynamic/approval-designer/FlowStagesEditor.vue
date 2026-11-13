<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import RowActionMenu from 'src/components/framework/RowActionMenu.vue';
import { insertStage, isSingleApprover, moveStage, removeStage, stageErrors, type StageDraft } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalStageDef, ApproverSource } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ApproverSourceEditor from './ApproverSourceEditor.vue';
import { ALL_SOURCE_KINDS } from './approver-source';
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
const { t, locale } = useI18n();
const $q = useQuasar();
const mobile = computed(() => $q.screen.lt.sm);

const fields = computed(() => toSchemaFields(props.module));
const allFields = computed(() => props.module.blocks.flatMap((b) => b.fields));
const fieldLabel = (id: number) => { const f = allFields.value.find((x) => x.id === id); return f ? resolveI18n(f.label, locale.value) : String(id); };
const editableOptions = computed(() => props.editableFieldIds.map((id) => ({ value: id, label: fieldLabel(id) })));
// A saved field that is no longer a candidate stays visible (as its name) so the designer sees what the stage still carries.
const editableOptionsOf = (s: StageDraft) => [...editableOptions.value, ...s.editableFields.filter((id) => !props.editableFieldIds.includes(id)).map((id) => ({ value: id, label: fieldLabel(id) }))];

// The backend only refuses a count above the limit that is also above the saved version's, so a flow saved with more stages may keep that many.
const atLimit = computed(() => props.limits.maxStages !== undefined && props.modelValue.length >= Math.max(props.limits.maxStages, props.saved?.length ?? 0));
// Same rule as the backend: a stage is matched to the saved one by its current code.
const savedOf = (s: StageDraft) => props.saved?.find((x) => x.code === s.code);
const codeChanged = (s: StageDraft) => !!s.savedCode && s.savedCode !== s.code;
// A code that is another saved stage's code gets that stage's relaxed limits.
const codeReused = (s: StageDraft) => s.savedCode !== s.code && !!savedOf(s);

const setAll = (draft: { stages: StageDraft[] }) => emit('update:modelValue', draft.stages);
const setApprovers = (i: number, v: ApproverSource) => patch(i, isSingleApprover(v) ? { approvers: v, mode: 'any' } : { approvers: v });
const patch = (i: number, p: Partial<StageDraft>) => emit('update:modelValue', props.modelValue.map((s, k) => (k === i ? { ...s, ...p } : s)));
const wrap = () => ({ stages: props.modelValue });
const insertAt = (at: number) => setAll(insertStage(wrap(), at, props.saved?.map((x) => x.code)));
const move = (i: number, d: -1 | 1) => setAll(moveStage(wrap(), i, d));
const remove = (i: number) => setAll(removeStage(wrap(), i));
const dueNum = (v: string | number | null) => (v === null || v === '' ? null : Number(v));

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

        <div class="column no-wrap q-gutter-y-sm">
          <I18nTextInput :model-value="s.name" :label="t('approval.designer.stage.name')" :error="errorsOf(i).name" @update:model-value="(v) => patch(i, { name: v })" />
          <q-input
            :model-value="s.code" :label="t('approval.designer.stage.code')" dense outlined :disable="disable" :data-testid="`stage-code-${i}`"
            :error="!!errorsOf(i).code || undefined" :error-message="errorsOf(i).code" @update:model-value="(v) => patch(i, { code: String(v ?? '') })"
          />
          <div v-if="codeChanged(s)" class="text-caption text-warning" :data-testid="`stage-code-warning-${i}`">{{ t('approval.designer.stage.codeChanged') }}</div>
          <div v-if="codeReused(s)" class="text-caption text-warning" :data-testid="`stage-code-reused-${i}`">{{ t('approval.designer.stage.codeReused', { code: s.code }) }}</div>

          <div>
            <div class="text-caption text-muted">{{ t('approval.designer.stage.when') }}</div>
            <ConditionEditor :fields="fields" allow-field-ref :model-value="s.when" @update:model-value="(v) => patch(i, { when: v })" />
            <div class="text-caption text-muted">{{ t('approval.designer.stage.whenHint') }}</div>
            <div v-if="errorsOf(i).when" class="text-caption text-negative">{{ errorsOf(i).when }}</div>
          </div>

          <ApproverSourceEditor
            :model-value="s.approvers" :module="module" :allowed="ALL_SOURCE_KINDS" :limits="limits" :saved="savedOf(s)?.approvers"
            :label="t('approval.designer.stage.source')" :error="errorsOf(i).approvers" :disable="disable" @update:model-value="(v) => setApprovers(i, v)"
          />

          <q-option-group
            v-if="!isSingleApprover(s.approvers)" :model-value="s.mode" type="radio" inline :disable="disable" role="radiogroup" :aria-label="t('approval.designer.stage.mode')"
            :options="[{ value: 'all', label: t('approval.designer.stage.modeAll') }, { value: 'any', label: t('approval.designer.stage.modeAny') }]"
            :data-testid="`stage-mode-${i}`" @update:model-value="(v: 'all' | 'any') => patch(i, { mode: v })"
          />
          <div v-if="errorsOf(i).mode" class="text-caption text-negative">{{ errorsOf(i).mode }}</div>

          <q-select
            :model-value="s.editableFields" :options="editableOptionsOf(s)" :label="t('approval.designer.stage.editableFields')" multiple use-chips emit-value map-options dense outlined
            :disable="disable" :hint="t('approval.designer.stage.editableFieldsHint')" :error="!!errorsOf(i).editableFields || undefined" :error-message="errorsOf(i).editableFields"
            :data-testid="`stage-editable-${i}`" @update:model-value="(v: number[]) => patch(i, { editableFields: v })"
          />

          <q-input
            :model-value="s.dueHours ?? ''" type="number" min="1" :max="limits.maxDueHours" :label="t('approval.designer.stage.dueHours')" dense outlined :disable="disable"
            :hint="limits.maxDueHours === undefined ? t('approval.designer.stage.dueHoursHint') : t('approval.designer.stage.dueHoursHintMax', { max: limits.maxDueHours })"
            :error="!!errorsOf(i).dueHours || undefined" :error-message="errorsOf(i).dueHours" :data-testid="`stage-due-${i}`"
            @update:model-value="(v) => patch(i, { dueHours: dueNum(v) })"
          />

          <q-option-group
            :model-value="s.onEmpty" type="radio" inline :disable="disable" role="radiogroup" :aria-label="t('approval.designer.stage.onEmpty')"
            :options="[{ value: 'fallback', label: t('approval.designer.stage.onEmptyFallback') }, { value: 'skip', label: t('approval.designer.stage.onEmptySkip') }]"
            :data-testid="`stage-onempty-${i}`" @update:model-value="(v: 'fallback' | 'skip') => patch(i, { onEmpty: v })"
          />
          <div v-if="s.onEmpty === 'skip'" class="text-caption text-warning" :data-testid="`stage-skip-warning-${i}`">{{ t('approval.designer.stage.skipWarning') }}</div>
          <div v-if="errorsOf(i).onEmpty" class="text-caption text-negative">{{ errorsOf(i).onEmpty }}</div>
          <div v-if="errorsOf(i)._" class="text-caption text-negative" :data-testid="`stage-error-${i}`">{{ errorsOf(i)._ }}</div>
        </div>
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
