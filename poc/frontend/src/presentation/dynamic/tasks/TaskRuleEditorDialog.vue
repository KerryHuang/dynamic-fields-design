<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useSaveTaskRule } from 'src/application/dynamic/queries/task-rules-queries';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import { isNestedCondition } from 'src/domain/dynamic/condition';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import {
  assigneeFields, blankTask, blankTaskRuleDraft, dueFields, fromTaskRule, REPEATS, toTaskRuleBody, TRIGGERS, triggerFields,
  validateDraft, type TaskRule, type TaskRuleDraft, type TaskRuleOn,
} from 'src/domain/dynamic/task-rule-draft';
import type { ConditionNode, SchemaField } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import { joinList } from 'src/i18n/translate';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{
  modelValue: boolean;
  moduleKey: string;
  fields: SchemaField[];
  /** Edit this rule; absent means a new one. */
  rule?: TaskRule;
  /** 這個模組的「每條規則任務數上限」生效值（政策 task.maxTasksPerRule）。 */
  maxTasks: number;
}>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean]; saved: [] }>();
const { t, locale } = useI18n();
const save = useSaveTaskRule(() => props.moduleKey);

const draft = ref<TaskRuleDraft>(blankTaskRuleDraft());
const errors = ref<string[]>([]);

watch(() => [props.modelValue, props.rule] as const, ([open]) => {
  if (!open) return;
  draft.value = props.rule ? fromTaskRule(props.rule) : blankTaskRuleDraft();
  errors.value = [];
}, { immediate: true });

const label = (f: SchemaField) => resolveI18n(f.label, locale.value);
const toOptions = (list: SchemaField[]) => list.map((f) => ({ value: String(f.id), label: label(f) }));
const assigneeOptions = computed(() => toOptions(assigneeFields(props.fields)));
const dueOptions = computed(() => toOptions(dueFields(props.fields)));
const triggerFieldOptions = computed(() => toOptions(triggerFields(props.fields)));
const triggerField = computed(() => props.fields.find((f) => f.id === draft.value.trigger.fieldId));
const triggerValueOptions = computed(() => {
  const f = triggerField.value;
  if (!f) return [];
  if (f.type === 'boolean') return [{ value: 'true', label: t('taskRules.valueTrue') }, { value: 'false', label: t('taskRules.valueFalse') }];
  return f.options.filter((o) => o.active).map((o) => ({ value: o.value, label: resolveI18n(o.label, locale.value) }));
});
const triggerValueText = computed(() => {
  const v = draft.value.trigger.value;
  return typeof v === 'boolean' ? String(v) : (v ?? null);
});

function setTrigger(on: TaskRuleOn) {
  draft.value.trigger = { on };
  // Creation fires once per record, so "every time" does not apply.
  if (on === 'onCreate') draft.value.repeat = 'once';
}
function setTriggerField(v: string | string[] | null) {
  draft.value.trigger = { on: 'onFieldBecomes', fieldId: typeof v === 'string' ? Number(v) : null, value: null };
}
function setTriggerValue(v: string | string[] | null) {
  if (typeof v !== 'string') draft.value.trigger.value = null;
  else draft.value.trigger.value = triggerField.value?.type === 'boolean' ? v === 'true' : v;
}
const pickId = (v: string | string[] | null) => (typeof v === 'string' ? Number(v) : null);

function addTask() {
  let n = draft.value.tasks.length + 1;
  while (draft.value.tasks.some((x) => x.code === `task${n}`)) n++;
  draft.value.tasks.push(blankTask(n));
}
const removeTask = (i: number) => draft.value.tasks.splice(i, 1);
const setWhen = (target: { when?: ConditionNode | null }, v: ConditionNode | undefined) => { target.when = v ?? null; };

function submit() {
  const problems = validateDraft(draft.value, props.fields, { maxTasks: props.maxTasks, originalTaskCount: props.rule?.tasks.length ?? 0 });
  errors.value = problems.map((k) => t(k, { max: props.maxTasks }));
  if (problems.length) return;
  save.mutate({ ruleKey: props.rule?.key, body: toTaskRuleBody(draft.value) }, {
    onSuccess: () => { emit('saved'); emit('update:modelValue', false); },
    onError: (e) => {
      const err = e as unknown as ApiError;
      const msgs = Object.values(err.errors ?? {}).flat();
      if (err.message) msgs.push(err.message);
      errors.value = msgs.length ? [joinList(msgs)] : [t('taskRules.saveFailed')];
    },
  });
}
</script>

<template>
  <q-dialog :model-value="modelValue" persistent @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="task-rule-dialog">
      <q-card-section class="text-h6">{{ rule ? t('taskRules.edit') : t('taskRules.add') }}</q-card-section>
      <q-card-section class="column no-wrap q-gutter-md scroll" style="max-height: 70vh" data-testid="task-rule-dialog-body">
        <q-input
          v-model="draft.key" :label="`${t('taskRules.key')} *`" :hint="t('taskRules.keyHint')" :disable="!!rule" outlined dense
          data-testid="task-rule-key"
        />
        <I18nTextInput v-model="draft.name" :label="t('taskRules.name')" data-testid="task-rule-name" />
        <q-toggle v-model="draft.active" :label="t('taskRules.active')" data-testid="task-rule-active" />

        <div>
          <div class="text-subtitle2">{{ t('taskRules.trigger.label') }}</div>
          <div class="column">
            <q-radio
              v-for="on in TRIGGERS" :key="on" :model-value="draft.trigger.on" :val="on" :label="t(`taskRules.trigger.${on}`)"
              :data-testid="`trigger-${on}`" @update:model-value="setTrigger(on)"
            />
          </div>
        </div>
        <template v-if="draft.trigger.on === 'onFieldBecomes'">
          <GenericSelect
            :model-value="draft.trigger.fieldId ? String(draft.trigger.fieldId) : null" :label="t('taskRules.triggerField')"
            :options="triggerFieldOptions" required data-testid="trigger-field" @update:model-value="setTriggerField"
          />
          <GenericSelect
            :model-value="triggerValueText" :label="t('taskRules.triggerValue')" :options="triggerValueOptions" required
            data-testid="trigger-value" @update:model-value="setTriggerValue"
          />
        </template>

        <div>
          <div class="text-subtitle2">{{ t('taskRules.repeat.label') }}</div>
          <div class="column">
            <q-radio
              v-for="r in REPEATS" :key="r" v-model="draft.repeat" :val="r" :label="t(`taskRules.repeat.${r}`)"
              :disable="r === 'everyTime' && draft.trigger.on === 'onCreate'" :data-testid="`repeat-${r}`"
            />
          </div>
        </div>

        <div data-testid="task-rule-when">
          <div class="text-subtitle2 q-mb-xs">
            {{ draft.trigger.on === 'onUpdateMatch' ? `${t('taskRules.condition')} *` : t('taskRules.condition') }}
          </div>
          <div v-if="isNestedCondition(draft.when ?? undefined)" class="text-muted">{{ t('taskRules.unsupported') }}</div>
          <ConditionEditor v-else :model-value="draft.when ?? undefined" :fields="fields" allow-field-ref @update:model-value="(v) => setWhen(draft, v)" />
        </div>

        <div class="text-subtitle2">{{ t('taskRules.tasks') }}</div>
        <q-card v-for="(task, i) in draft.tasks" :key="i" flat bordered :data-testid="`task-rule-task-${i}`">
          <q-card-section class="column q-gutter-sm">
            <div class="row items-center no-wrap">
              <q-input v-model="task.code" :label="`${t('taskRules.taskCode')} *`" outlined dense class="col" :data-testid="`task-code-${i}`" />
              <q-btn
                flat dense round icon="delete" color="negative" :title="t('taskRules.removeTask')" :disable="draft.tasks.length <= 1"
                :data-testid="`task-remove-${i}`" @click="removeTask(i)"
              />
            </div>
            <I18nTextInput v-model="task.title" :label="t('taskRules.taskTitle')" :data-testid="`task-title-${i}`" />
            <I18nTextInput :model-value="task.description ?? {}" :label="t('taskRules.taskDescription')" optional @update:model-value="(v) => (task.description = v)" />
            <GenericSelect
              :model-value="task.assigneeFieldId ? String(task.assigneeFieldId) : null" :label="t('taskRules.assigneeField')"
              :options="assigneeOptions" required :data-testid="`task-assignee-${i}`"
              @update:model-value="(v) => (task.assigneeFieldId = pickId(v) ?? 0)"
            />
            <GenericSelect
              :model-value="task.dueFieldId ? String(task.dueFieldId) : null" :label="t('taskRules.dueField')"
              :options="dueOptions" :data-testid="`task-due-${i}`" @update:model-value="(v) => (task.dueFieldId = pickId(v))"
            />
            <q-input
              v-model.number="task.dueOffsetDays" type="number" :label="t('taskRules.offsetDays')" :hint="t('taskRules.offsetHint')"
              outlined dense :data-testid="`task-offset-${i}`"
            />
            <q-expansion-item dense :label="t('taskRules.taskCondition')">
              <div v-if="isNestedCondition(task.when ?? undefined)" class="text-muted">{{ t('taskRules.unsupported') }}</div>
              <ConditionEditor v-else :model-value="task.when ?? undefined" :fields="fields" allow-field-ref @update:model-value="(v) => setWhen(task, v)" />
            </q-expansion-item>
          </q-card-section>
        </q-card>
        <div>
          <q-btn
            flat dense color="primary" icon="add" :label="t('taskRules.addTask')" :disable="draft.tasks.length >= maxTasks"
            data-testid="task-add" @click="addTask"
          />
        </div>

        <div v-if="errors.length" class="text-negative" data-testid="task-rule-errors">
          <div v-for="e in errors" :key="e">{{ e }}</div>
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('designer.close')" data-testid="task-rule-cancel" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" :label="t('designer.save')" :loading="save.isPending.value" data-testid="task-rule-save" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.task-rule-dialog { width: 720px; max-width: 95vw; }
</style>
