<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useEvents, useHiddenFieldIds, useSaveEventMutation } from 'src/application/dynamic/queries/events-queries';
import { useModules } from 'src/application/dynamic/queries/modules-queries';
import { useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import {
  buildEvent, draftFromEvent, emptyEffect, emptyEventDraft, groupEventErrors, MAX_EFFECTS, nextEffectCode, setIsComplete,
  type EffectDraft, type EventDraft,
} from 'src/domain/dynamic/event-draft';
import type { EventTriggerOn, ModuleEvent } from 'src/domain/dynamic/events-ledger';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, ModuleDetail } from 'src/domain/dynamic/types';
import DesignerDialog from 'src/presentation/dynamic/designer/DesignerDialog.vue';
import { fieldOptions } from 'src/presentation/dynamic/designer/field-options';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import type { ApiError } from 'src/services/api/http';
import EventEffectEditor from './EventEffectEditor.vue';

const props = defineProps<{ modelValue: boolean; module: ModuleDetail; event?: ModuleEvent }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const form = reactive<EventDraft>(emptyEventDraft());
const error = ref<ApiError | null>(null);
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
const isEdit = computed(() => !!props.event);

watch(() => [props.modelValue, props.event?.key] as const, ([open]) => {
  if (!open) return;
  error.value = null;
  Object.assign(form, props.event ? draftFromEvent(props.event) : emptyEventDraft());
  // Object.assign keeps a stale optional `when` when the new draft has none
  if (!props.event?.definition.when) delete form.when;
}, { immediate: true });

const sourceKey = computed(() => props.module.key);
const modules = useModules();
const events = useEvents(sourceKey);
const workflow = useWorkflowQuery(sourceKey);
const hiddenHeader = useHiddenFieldIds(sourceKey);

// ---- trigger ----
const hasWorkflow = computed(() => !!workflow.data.value);
const triggerOptions = computed(() => (['create', 'update', 'transition'] as const).map((value) => ({
  value, label: t(`events.triggerKind.${value}`), ...(value === 'transition' && !hasWorkflow.value && { disable: true }),
})));
const stateOptions = computed(() => {
  const field = props.module.blocks.flatMap((b) => b.fields).find((f) => f.id === workflow.data.value?.statusFieldId);
  return (field?.options ?? []).filter((o) => workflow.data.value?.states.some((s) => s.optionId === o.id))
    .map((o) => ({ value: o.id, label: resolveI18n(o.label, locale.value) }));
});
const watchOptions = computed(() => fieldOptions(props.module, locale.value));
const conditionFields = computed(() => toSchemaFields(props.module));

const triggerSel = useSelectFilter(triggerOptions);
const fromSel = useSelectFilter(stateOptions);
const toSel = useSelectFilter(stateOptions);
const watchSel = useSelectFilter(watchOptions);

const setWhen = (n: ConditionNode | undefined) => { if (n) form.when = n; else delete form.when; };

// ---- effects ----
const addEffect = () => { form.effects.push(emptyEffect(nextEffectCode(form.effects))); };
const setEffect = (i: number, e: EffectDraft) => { form.effects[i] = e; error.value = null; };
const removeEffect = (i: number) => { form.effects.splice(i, 1); error.value = null; };
const savedEvents = computed(() => events.data.value ?? []);

// ---- save ----
const save = useSaveEventMutation();
const body = computed(() => buildEvent(form, props.module.key));
const incomplete = computed(() => form.effects.some((e) => !e.custom && e.set.some((s) => !setIsComplete(s))));
async function submit() {
  if (!(await nameInput.value?.validate()) || !body.value) return;
  error.value = null;
  save.mutate({ event: body.value, isNew: !isEdit.value }, {
    onSuccess: (saved) => {
      emit('update:modelValue', false);
      const warnings = saved.warnings ?? [];
      $q.notify({ type: warnings.length ? 'warning' : 'positive', message: t(warnings.length ? 'events.warnings' : 'events.saved') });
      for (const message of warnings) $q.notify({ type: 'warning', message });
    },
    onError: (e) => {
      const err = e as unknown as ApiError;
      error.value = err;
      if (!Object.keys(err.errors ?? {}).length) $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
    },
  });
}
const errors = computed(() => groupEventErrors(error.value?.errors));
const joined = (lines: string[]) => lines.join('、') || undefined;
</script>

<template>
  <DesignerDialog :model-value="modelValue" :title="isEdit ? t('events.edit') : t('events.create')" @update:model-value="(v) => emit('update:modelValue', v)">
    <div data-testid="event-key">
      <q-input
        v-model="form.key" :label="t('events.key')" :hint="t('events.keyHint')" :readonly="isEdit" outlined dense
        :error="!!errors.key.length || undefined" :error-message="joined(errors.key)"
      />
    </div>
    <div data-testid="event-name"><I18nTextInput ref="nameInput" v-model="form.name" :label="t('events.name')" :error="joined(errors.name)" /></div>
    <q-toggle v-model="form.active" :label="t('events.active')" data-testid="event-active" />

    <div class="column q-gutter-y-sm">
      <div class="text-subtitle1">{{ t('events.trigger') }}</div>
      <q-select
        :model-value="form.on" :options="triggerSel.filtered.value" :label="t('events.triggerOn')"
        :hint="hasWorkflow ? undefined : t('events.noWorkflow')" emit-value map-options outlined dense
        :data-testid="'event-trigger'" @update:model-value="(v: EventTriggerOn) => (form.on = v)"
      />
      <template v-if="form.on === 'transition'">
        <q-select
          :model-value="form.fromStates" :options="fromSel.filtered.value" :label="t('events.fromStates')" :hint="t('events.fromStatesHint')"
          multiple use-chips emit-value map-options outlined dense use-input input-debounce="0" data-testid="event-from-states"
          @filter="fromSel.onFilter" @popup-hide="fromSel.reset" @update:model-value="(v: number[]) => (form.fromStates = v)"
        >
          <template #no-option><SelectNoOption /></template>
        </q-select>
        <q-select
          :model-value="form.toState" :options="toSel.filtered.value" :label="t('events.toState')" emit-value map-options outlined dense
          use-input input-debounce="0" fill-input hide-selected data-testid="event-to-state"
          @filter="toSel.onFilter" @popup-hide="toSel.reset" @update:model-value="(v: number) => (form.toState = v)"
        >
          <template #no-option><SelectNoOption /></template>
        </q-select>
      </template>
      <q-select
        v-else-if="form.on === 'update'" :model-value="form.watch" :options="watchSel.filtered.value" :label="t('events.watch')" :hint="t('events.watchHint')"
        multiple use-chips emit-value map-options outlined dense use-input input-debounce="0" data-testid="event-watch"
        @filter="watchSel.onFilter" @popup-hide="watchSel.reset" @update:model-value="(v: number[]) => (form.watch = v)"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>
      <div v-if="errors.trigger.length" class="text-negative" data-testid="event-trigger-error">{{ errors.trigger.join('、') }}</div>
    </div>

    <div>
      <div class="text-subtitle1 q-mb-xs">{{ t('events.when') }}</div>
      <ConditionEditor :model-value="form.when" :fields="conditionFields" @update:model-value="setWhen" />
      <div class="text-caption text-muted q-mt-xs">{{ t('events.whenHint') }}</div>
      <div v-if="errors.when.length" class="text-negative" data-testid="event-when-error">{{ errors.when.join('、') }}</div>
    </div>

    <div class="column q-gutter-y-sm">
      <div class="row items-center">
        <div class="text-subtitle1">{{ t('events.effects') }}</div>
        <q-space />
        <q-btn
          dense color="primary" icon="add" :label="t('events.addEffect')" :disable="form.effects.length >= MAX_EFFECTS" data-testid="event-add-effect" @click="addEffect"
        />
      </div>
      <div class="text-caption text-muted">{{ t('events.effectsHint', { max: MAX_EFFECTS }) }}</div>
      <div v-if="!form.effects.length" class="text-muted">{{ t('events.noEffects') }}</div>
      <EventEffectEditor
        v-for="(effect, i) in form.effects" :key="i" :model-value="effect" :index="i" :source-module="module" :modules="modules.data.value ?? []"
        :events="savedEvents" :hidden-header="hiddenHeader" :errors="errors.effects.get(i)"
        @update:model-value="(e) => setEffect(i, e)" @remove="removeEffect(i)"
      />
    </div>

    <div v-if="errors.general.length" class="text-negative" data-testid="event-errors">
      <div v-for="line in errors.general" :key="line">{{ line }}</div>
    </div>
    <div v-if="incomplete" class="text-warning" data-testid="event-incomplete">{{ t('events.setIncomplete') }}</div>

    <template #actions>
      <q-btn flat :label="t('designer.close')" data-testid="event-cancel" @click="emit('update:modelValue', false)" />
      <q-btn color="primary" :label="t('designer.save')" :disable="!body" :loading="save.isPending.value" data-testid="event-save" @click="submit" />
    </template>
  </DesignerDialog>
</template>
