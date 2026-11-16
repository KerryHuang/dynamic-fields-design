<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { joinList } from 'src/i18n/translate';
import { useSaveConversionMutation } from 'src/application/dynamic/queries/conversions-queries';
import { useModuleDetail, useModules } from 'src/application/dynamic/queries/modules-queries';
import { useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { buildConversion, draftFromConversion, emptyConversionDraft, type ConversionDraft } from 'src/domain/dynamic/conversion';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { Conversion } from 'src/domain/dynamic/types';
import { copySource, copyTarget, fieldOptions, isIntegerField, isQuantityField } from 'src/presentation/dynamic/designer/field-options';
import FieldMapEditor from 'src/presentation/dynamic/designer/FieldMapEditor.vue';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ modelValue: boolean; conversion?: Conversion }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const form = reactive<ConversionDraft>(emptyConversionDraft());
const error = ref<ApiError | null>(null);
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
const isEdit = computed(() => !!props.conversion);

watch(() => [props.modelValue, props.conversion?.key] as const, ([open]) => {
  if (!open) return;
  error.value = null;
  Object.assign(form, props.conversion ? draftFromConversion(props.conversion) : emptyConversionDraft());
}, { immediate: true });

// ---- modules: header pairs must be active standalone modules; line modules are the details of each ----
const modules = useModules();
const nameOf = (m: { name: Conversion['name'] }) => resolveI18n(m.name, locale.value);
const headerModules = computed(() => (modules.data.value ?? [])
  .filter((m) => m.status === 'Active' && m.kind !== 'extension' && !m.detailOf)
  .map((m) => ({ value: m.key, label: `${nameOf(m)} (${m.key})` })));
const detailModulesOf = (header: string | null) => (modules.data.value ?? [])
  .filter((m) => !!header && m.detailOf === header && m.status === 'Active')
  .map((m) => ({ value: m.key, label: `${nameOf(m)} (${m.key})` }));
const sourceLineModules = computed(() => detailModulesOf(form.sourceModuleKey));
const targetLineModules = computed(() => detailModulesOf(form.targetModuleKey));

const detailOf = (key: () => string | null) => useModuleDetail(computed(() => key() ?? ''));
const source = detailOf(() => form.sourceModuleKey);
const target = detailOf(() => form.targetModuleKey);
const lineSource = detailOf(() => (form.withLines ? form.linesSourceModuleKey : null));
const lineTarget = detailOf(() => (form.withLines ? form.linesTargetModuleKey : null));

// A single candidate is picked for the user; a stale pick (module changed) is cleared.
function syncLineModules() {
  const pick = (current: string | null, options: { value: string }[]) =>
    options.some((o) => o.value === current) ? current : (options.length === 1 ? options[0]!.value : null);
  form.linesSourceModuleKey = pick(form.linesSourceModuleKey, sourceLineModules.value);
  form.linesTargetModuleKey = pick(form.linesTargetModuleKey, targetLineModules.value);
}
watch([() => form.withLines, sourceLineModules, targetLineModules], () => { if (form.withLines) syncLineModules(); });

// ---- field options ----
const workflow = useWorkflowQuery(computed(() => form.sourceModuleKey ?? ''));
const stateOptions = computed(() => {
  const field = source.data.value?.blocks.flatMap((b) => b.fields).find((f) => f.id === workflow.data.value?.statusFieldId);
  return (field?.options ?? []).filter((o) => workflow.data.value?.states.some((s) => s.optionId === o.id))
    .map((o) => ({ value: o.id, label: resolveI18n(o.label, locale.value) }));
});
const hasSourceWorkflow = computed(() => !!workflow.data.value);
const tracingFieldOptions = computed(() => fieldOptions(target.data.value, locale.value,
  (f) => f.type === 'reference' && f.config.refTarget?.kind === 'module' && f.config.refTarget.key === form.sourceModuleKey));
const headerFromOptions = computed(() => fieldOptions(source.data.value, locale.value, copySource));
const headerToOptions = computed(() => fieldOptions(target.data.value, locale.value, (f) => copyTarget(f) && f.id !== form.headerSourceFieldId));
const lineTracingOptions = computed(() => fieldOptions(lineTarget.data.value, locale.value, isIntegerField));
const quantityFromOptions = computed(() => fieldOptions(lineSource.data.value, locale.value, isQuantityField));
const quantityToOptions = computed(() => fieldOptions(lineTarget.data.value, locale.value, isQuantityField));
const linesFromOptions = computed(() => fieldOptions(lineSource.data.value, locale.value, copySource));
const linesToOptions = computed(() => fieldOptions(lineTarget.data.value, locale.value, (f) => copyTarget(f) && f.id !== form.sourceLineFieldId));

const sourceSel = useSelectFilter(headerModules);
const targetSel = useSelectFilter(headerModules);
const stateSel = useSelectFilter(stateOptions);
const tracingSel = useSelectFilter(tracingFieldOptions);
const lsModSel = useSelectFilter(sourceLineModules);
const ltModSel = useSelectFilter(targetLineModules);
const lineTracingSel = useSelectFilter(lineTracingOptions);
const qFromSel = useSelectFilter(quantityFromOptions);
const qToSel = useSelectFilter(quantityToOptions);

// Everything below the module pick was chosen against the old modules.
function setSource(k: string | null) {
  Object.assign(form, { sourceModuleKey: k, sourceStates: [], headerSourceFieldId: null, headerCopy: [], linesSourceModuleKey: null, quantityFrom: null, linesCopy: [] });
  if (form.withLines) syncLineModules();
}
function setTarget(k: string | null) {
  Object.assign(form, { targetModuleKey: k, headerSourceFieldId: null, headerCopy: [], linesTargetModuleKey: null, sourceLineFieldId: null, quantityTo: null, linesCopy: [] });
  if (form.withLines) syncLineModules();
}
const clearLineFields = () => Object.assign(form, { quantityFrom: null, linesCopy: [] });
const clearLineTargetFields = () => Object.assign(form, { sourceLineFieldId: null, quantityTo: null, linesCopy: [] });

// ---- save ----
const save = useSaveConversionMutation();
const body = computed(() => buildConversion(form));
async function submit() {
  if (!(await nameInput.value?.validate()) || !body.value) return;
  error.value = null;
  save.mutate({ conversion: body.value, isNew: !isEdit.value }, {
    onSuccess: () => { emit('update:modelValue', false); $q.notify({ type: 'positive', message: t('docflow.conversion.saved') }); },
    onError: (e) => {
      const err = e as unknown as ApiError;
      error.value = err;
      if (!Object.keys(err.errors ?? {}).length) $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
    },
  });
}
// Key and name errors sit on their inputs; the rest (definition paths such as header.copy[0]) are listed under the form.
const errorsOf = (prefix: string) => Object.entries(error.value?.errors ?? {}).filter(([k]) => k.toLowerCase().startsWith(prefix));
const keyError = computed(() => joinList(errorsOf('key').flatMap(([, v]) => v)) || undefined);
const nameError = computed(() => joinList(errorsOf('name').flatMap(([, v]) => v)) || undefined);
const definitionErrors = computed(() => Object.entries(error.value?.errors ?? {})
  .filter(([k]) => !/^(key|name)/i.test(k)).flatMap(([, v]) => v));
</script>

<template>
  <q-dialog :model-value="modelValue" persistent @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card style="min-width: 640px; max-width: 95vw">
      <q-card-section class="text-h6">{{ isEdit ? t('docflow.conversion.edit') : t('docflow.conversion.create') }}</q-card-section>
      <q-card-section class="q-gutter-md scroll" style="max-height: 70vh">
        <q-input
          v-model="form.key" :label="t('docflow.conversion.key')" :hint="t('docflow.conversion.keyHint')" :readonly="isEdit" outlined dense
          data-testid="conversion-key" :error="!!keyError || undefined" :error-message="keyError"
        />
        <I18nTextInput ref="nameInput" v-model="form.name" :label="t('docflow.conversion.name')" :error="nameError" />
        <q-toggle v-model="form.active" :label="t('docflow.conversion.active')" data-testid="conversion-active" />

        <div class="row q-col-gutter-md">
          <div class="col-12 col-sm-6" data-testid="conversion-source">
            <q-select
              :model-value="form.sourceModuleKey" :options="sourceSel.filtered.value" :label="t('docflow.conversion.source')" :readonly="isEdit"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected
              @filter="sourceSel.onFilter" @popup-hide="sourceSel.reset" @update:model-value="setSource"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div class="col-12 col-sm-6" data-testid="conversion-target">
            <q-select
              :model-value="form.targetModuleKey" :options="targetSel.filtered.value" :label="t('docflow.conversion.target')" :readonly="isEdit"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected
              @filter="targetSel.onFilter" @popup-hide="targetSel.reset" @update:model-value="setTarget"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
        </div>

        <div v-if="form.sourceModuleKey" data-testid="conversion-states">
          <q-select
            v-model="form.sourceStates" :options="stateSel.filtered.value" :label="t('docflow.conversion.sourceStates')"
            :hint="hasSourceWorkflow ? t('docflow.conversion.sourceStatesHint') : t('docflow.conversion.noSourceWorkflow')" :disable="!hasSourceWorkflow"
            multiple use-chips emit-value map-options outlined dense use-input input-debounce="0"
            @filter="stateSel.onFilter" @popup-hide="stateSel.reset"
          >
            <template #no-option><SelectNoOption /></template>
          </q-select>
        </div>

        <template v-if="form.sourceModuleKey && form.targetModuleKey">
          <div class="q-gutter-sm" data-testid="conversion-header">
            <div class="text-subtitle1">{{ t('docflow.conversion.header') }}</div>
            <q-select
              v-model="form.headerSourceFieldId" :options="tracingSel.filtered.value" :label="t('docflow.conversion.headerSourceField')"
              :hint="tracingFieldOptions.length ? t('docflow.conversion.headerSourceFieldHint') : t('docflow.conversion.noTracingField')"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="conversion-header-source-field"
              @filter="tracingSel.onFilter" @popup-hide="tracingSel.reset"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
            <div class="text-caption text-muted">{{ t('docflow.conversion.copy') }}</div>
            <FieldMapEditor
              v-model="form.headerCopy" :from-options="headerFromOptions" :to-options="headerToOptions"
              :from-label="t('docflow.conversion.fromSource')" :to-label="t('docflow.conversion.toTarget')"
            />
          </div>

          <div class="q-gutter-sm" data-testid="conversion-lines">
            <q-toggle v-model="form.withLines" :label="t('docflow.conversion.withLines')" data-testid="conversion-with-lines" />
            <div class="text-caption text-muted">{{ t('docflow.conversion.withLinesHint') }}</div>
            <template v-if="form.withLines">
              <div class="row q-col-gutter-md">
                <div class="col-12 col-sm-6">
                  <q-select
                    v-model="form.linesSourceModuleKey" :options="lsModSel.filtered.value" :label="t('docflow.conversion.linesSource')"
                    :hint="sourceLineModules.length ? undefined : t('docflow.conversion.noDetailModule')" emit-value map-options outlined dense
                    use-input input-debounce="0" fill-input hide-selected data-testid="conversion-lines-source"
                    @filter="lsModSel.onFilter" @popup-hide="lsModSel.reset" @update:model-value="clearLineFields"
                  >
                    <template #no-option><SelectNoOption /></template>
                  </q-select>
                </div>
                <div class="col-12 col-sm-6">
                  <q-select
                    v-model="form.linesTargetModuleKey" :options="ltModSel.filtered.value" :label="t('docflow.conversion.linesTarget')"
                    :hint="targetLineModules.length ? undefined : t('docflow.conversion.noDetailModule')" emit-value map-options outlined dense
                    use-input input-debounce="0" fill-input hide-selected data-testid="conversion-lines-target"
                    @filter="ltModSel.onFilter" @popup-hide="ltModSel.reset" @update:model-value="clearLineTargetFields"
                  >
                    <template #no-option><SelectNoOption /></template>
                  </q-select>
                </div>
              </div>
              <q-select
                v-model="form.sourceLineFieldId" :options="lineTracingSel.filtered.value" :label="t('docflow.conversion.sourceLineField')"
                :hint="t('docflow.conversion.sourceLineFieldHint')" emit-value map-options outlined dense
                use-input input-debounce="0" fill-input hide-selected data-testid="conversion-source-line-field"
                @filter="lineTracingSel.onFilter" @popup-hide="lineTracingSel.reset"
              >
                <template #no-option><SelectNoOption /></template>
              </q-select>
              <div class="row q-col-gutter-md">
                <div class="col-12 col-sm-6">
                  <q-select
                    v-model="form.quantityFrom" :options="qFromSel.filtered.value" :label="t('docflow.conversion.quantityFrom')" emit-value map-options
                    outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="conversion-quantity-from"
                    @filter="qFromSel.onFilter" @popup-hide="qFromSel.reset"
                  >
                    <template #no-option><SelectNoOption /></template>
                  </q-select>
                </div>
                <div class="col-12 col-sm-6">
                  <q-select
                    v-model="form.quantityTo" :options="qToSel.filtered.value" :label="t('docflow.conversion.quantityTo')" emit-value map-options
                    outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="conversion-quantity-to"
                    @filter="qToSel.onFilter" @popup-hide="qToSel.reset"
                  >
                    <template #no-option><SelectNoOption /></template>
                  </q-select>
                </div>
              </div>
              <div class="text-caption text-muted">{{ t('docflow.conversion.linesCopy') }}</div>
              <FieldMapEditor
                v-model="form.linesCopy" :from-options="linesFromOptions" :to-options="linesToOptions"
                :from-label="t('docflow.conversion.fromSource')" :to-label="t('docflow.conversion.toTarget')"
              />
            </template>
          </div>
        </template>

        <div v-if="definitionErrors.length" class="text-negative" data-testid="conversion-errors">
          <div v-for="line in definitionErrors" :key="line">{{ line }}</div>
        </div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('designer.close')" data-testid="conversion-cancel" @click="emit('update:modelValue', false)" />
        <q-btn color="primary" :label="t('designer.save')" :disable="!body" :loading="save.isPending.value" data-testid="conversion-save" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
