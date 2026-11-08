<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useSaveCheckMutation } from 'src/application/dynamic/queries/checks-queries';
import { useModuleDetail } from 'src/application/dynamic/queries/modules-queries';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { buildCheck, draftFromCheck, emptyCheckDraft, groupCheckErrors, type CheckDraft } from 'src/domain/dynamic/check-draft';
import type { CheckOp, ModuleCheck } from 'src/domain/dynamic/events-ledger';
import type { ConditionNode, ModuleDetail } from 'src/domain/dynamic/types';
import DesignerDialog from 'src/presentation/dynamic/designer/DesignerDialog.vue';
import { fieldOptions } from 'src/presentation/dynamic/designer/field-options';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import ConditionEditor from 'src/presentation/dynamic/list/ConditionEditor.vue';
import type { ApiError } from 'src/services/api/http';

/** Group keys must be indexed types; sums read number fields. */
const GROUP_TYPES = ['reference', 'enum', 'text', 'integer', 'date', 'autonumber'];
const NUMBER_TYPES = ['integer', 'decimal', 'currency'];
const OPS: CheckOp[] = ['gte', 'gt', 'lte', 'lt', 'eq'];

const props = defineProps<{ modelValue: boolean; module: ModuleDetail; check?: ModuleCheck }>();
const emit = defineEmits<{ 'update:modelValue': [open: boolean] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const form = reactive<CheckDraft>(emptyCheckDraft());
const error = ref<ApiError | null>(null);
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
const messageInput = ref<{ validate(): Promise<boolean> } | null>(null);
const isEdit = computed(() => !!props.check);

watch(() => [props.modelValue, props.check?.key] as const, ([open]) => {
  if (!open) return;
  error.value = null;
  Object.assign(form, props.check ? draftFromCheck(props.check) : emptyCheckDraft());
  // Object.assign keeps a stale optional `where` when the new draft has none
  if (!props.check || !props.check.definition.where) delete form.where;
}, { immediate: true });

// ---- options ----
const groupOptions = computed(() => fieldOptions(props.module, locale.value, (f) => GROUP_TYPES.includes(f.type)));
const numberOptions = computed(() => fieldOptions(props.module, locale.value, (f) => NUMBER_TYPES.includes(f.type)));
const refOptions = computed(() => fieldOptions(props.module, locale.value, (f) => f.type === 'reference' && f.config.refTarget?.kind === 'module'));
const targetKey = computed(() => {
  const f = props.module.blocks.flatMap((b) => b.fields).find((x) => x.id === form.limitVia);
  return form.limitKind === 'via' && f?.config.refTarget?.kind === 'module' ? f.config.refTarget.key : '';
});
const target = useModuleDetail(targetKey);
const targetNumberOptions = computed(() => fieldOptions(target.data.value, locale.value, (f) => NUMBER_TYPES.includes(f.type)));
const conditionFields = computed(() => toSchemaFields(props.module));
const opOptions = computed(() => OPS.map((value) => ({ value, label: t(`checks.ops.${value}`) })));
const limitKinds = computed(() => (['const', 'field', 'via'] as const).map((value) => ({ value, label: t(`checks.limit.${value}`) })));
const functions = computed(() => (['sum', 'count'] as const).map((value) => ({ value, label: t(`checks.fn.${value}`) })));

const groupSel = useSelectFilter(groupOptions);
const fieldSel = useSelectFilter(numberOptions);
const opSel = useSelectFilter(opOptions);
const ownSel = useSelectFilter(numberOptions);
const viaSel = useSelectFilter(refOptions);
const viaFieldSel = useSelectFilter(targetNumberOptions);

const setKind = (k: CheckDraft['limitKind']) => Object.assign(form, { limitKind: k, limitField: null, limitVia: null, limitConst: null });
const setVia = (id: number) => Object.assign(form, { limitVia: id, limitField: null });
const setConst = (v: string | number | null) => { form.limitConst = v === '' || v === null ? null : Number(v); };
const setWhere = (n: ConditionNode | undefined) => { if (n) form.where = n; else delete form.where; };

// ---- save ----
const save = useSaveCheckMutation();
const body = computed(() => buildCheck(form, props.module.key));
async function submit() {
  const names = await Promise.all([nameInput.value?.validate(), messageInput.value?.validate()]);
  if (names.some((ok) => !ok) || !body.value) return;
  error.value = null;
  save.mutate({ check: body.value, isNew: !isEdit.value }, {
    onSuccess: (saved) => {
      emit('update:modelValue', false);
      $q.notify({ type: 'positive', message: t('checks.saved') });
      // e.g. "filtering was switched on for a field": a side effect the designer did not ask for
      for (const message of saved.notices ?? []) $q.notify({ type: 'info', message });
    },
    onError: (e) => {
      const err = e as unknown as ApiError;
      error.value = err;
      if (!Object.keys(err.errors ?? {}).length) $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
    },
  });
}
const errors = computed(() => groupCheckErrors(error.value?.errors));
const joined = (lines: string[]) => lines.join('、') || undefined;
</script>

<template>
  <DesignerDialog :model-value="modelValue" :title="isEdit ? t('checks.edit') : t('checks.create')" @update:model-value="(v) => emit('update:modelValue', v)">
    <div data-testid="check-key">
      <q-input
        v-model="form.key" :label="t('checks.key')" :hint="t('checks.keyHint')" :readonly="isEdit" outlined dense
        :error="!!errors.key.length || undefined" :error-message="joined(errors.key)"
      />
    </div>
    <div data-testid="check-name"><I18nTextInput ref="nameInput" v-model="form.name" :label="t('checks.name')" :error="joined(errors.name)" /></div>
    <q-toggle v-model="form.active" :label="t('checks.active')" data-testid="check-active" />

    <div data-testid="check-group-by-wrap">
      <q-select
        :model-value="form.groupBy" :options="groupSel.filtered.value" :label="t('checks.groupBy')" :hint="t('checks.groupByHint')"
        :error="!!errors.groupBy.length || undefined" :error-message="joined(errors.groupBy)"
        multiple use-chips emit-value map-options outlined dense use-input input-debounce="0" data-testid="check-group-by"
        @filter="groupSel.onFilter" @popup-hide="groupSel.reset" @update:model-value="(v: number[]) => (form.groupBy = v)"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>
    </div>

    <div class="row q-col-gutter-md items-start">
      <div class="col-12 col-sm-5">
        <div class="text-caption text-muted">{{ t('checks.function') }}</div>
        <q-btn-toggle
          :model-value="form.function" :options="functions" no-caps unelevated toggle-color="primary" data-testid="check-function"
          @update:model-value="(v: 'sum' | 'count') => (form.function = v)"
        />
      </div>
      <div v-if="form.function === 'sum'" class="col-12 col-sm-7">
        <q-select
          :model-value="form.fieldId" :options="fieldSel.filtered.value" :label="t('checks.field')" :hint="t('checks.fieldHint')"
          :error="!!errors.field.length || undefined" :error-message="joined(errors.field)"
          emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="check-field"
          @filter="fieldSel.onFilter" @popup-hide="fieldSel.reset" @update:model-value="(v: number) => (form.fieldId = v)"
        >
          <template #no-option><SelectNoOption /></template>
        </q-select>
      </div>
    </div>

    <div>
      <div class="text-subtitle2 q-mb-xs">{{ t('checks.where') }}</div>
      <ConditionEditor :model-value="form.where" :fields="conditionFields" @update:model-value="setWhere" />
      <div class="text-caption text-muted q-mt-xs">{{ t('checks.whereHint') }}</div>
      <div v-if="errors.where.length" class="text-negative text-caption" data-testid="check-where-error">{{ errors.where.join('、') }}</div>
    </div>

    <div>
      <div class="text-subtitle2 q-mb-xs">{{ t('checks.assert') }}</div>
      <div class="row q-col-gutter-md items-start">
        <div class="col-12 col-sm-4">
          <q-select
            :model-value="form.op" :options="opSel.filtered.value" :label="t('checks.op')" emit-value map-options outlined dense
            use-input input-debounce="0" fill-input hide-selected data-testid="check-op"
            @filter="opSel.onFilter" @popup-hide="opSel.reset" @update:model-value="(v: CheckOp) => (form.op = v)"
          />
        </div>
        <div class="col-12 col-sm-8">
          <div class="text-caption text-muted">{{ t('checks.limitKind') }}</div>
          <q-btn-toggle
            :model-value="form.limitKind" :options="limitKinds" no-caps unelevated toggle-color="primary" data-testid="check-limit-kind"
            @update:model-value="setKind"
          />
        </div>
      </div>
      <div class="row q-col-gutter-md items-start q-mt-none">
        <div v-if="form.limitKind === 'const'" class="col-12 col-sm-6">
          <q-input
            :model-value="form.limitConst" type="number" step="any" :label="t('checks.const')" outlined dense data-testid="check-limit-const"
            @update:model-value="setConst"
          />
        </div>
        <div v-else-if="form.limitKind === 'field'" class="col-12 col-sm-6">
          <q-select
            :model-value="form.limitField" :options="ownSel.filtered.value" :label="t('checks.ownField')" emit-value map-options outlined dense
            use-input input-debounce="0" fill-input hide-selected data-testid="check-limit-field"
            @filter="ownSel.onFilter" @popup-hide="ownSel.reset" @update:model-value="(v: number) => (form.limitField = v)"
          >
            <template #no-option><SelectNoOption /></template>
          </q-select>
        </div>
        <template v-else>
          <div class="col-12 col-sm-6">
            <q-select
              :model-value="form.limitVia" :options="viaSel.filtered.value" :label="t('checks.via')" emit-value map-options outlined dense
              use-input input-debounce="0" fill-input hide-selected data-testid="check-limit-via"
              @filter="viaSel.onFilter" @popup-hide="viaSel.reset" @update:model-value="setVia"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div class="col-12 col-sm-6">
            <q-select
              :model-value="form.limitField" :options="viaFieldSel.filtered.value" :label="t('checks.viaField')" :disable="form.limitVia == null"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="check-limit-via-field"
              @filter="viaFieldSel.onFilter" @popup-hide="viaFieldSel.reset" @update:model-value="(v: number) => (form.limitField = v)"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
        </template>
      </div>
      <div v-if="errors.assert.length" class="text-negative text-caption" data-testid="check-assert-error">{{ errors.assert.join('、') }}</div>
    </div>

    <div data-testid="check-message">
      <I18nTextInput ref="messageInput" v-model="form.message" :label="t('checks.message')" :error="joined(errors.message)" />
      <div class="text-caption text-muted q-mt-xs">{{ t('checks.messageHint') }}</div>
    </div>

    <div v-if="errors.general.length" class="text-negative" data-testid="check-errors">
      <div v-for="line in errors.general" :key="line">{{ line }}</div>
    </div>

    <template #actions>
      <q-btn flat :label="t('designer.close')" data-testid="check-cancel" @click="emit('update:modelValue', false)" />
      <q-btn color="primary" :label="t('designer.save')" :disable="!body" :loading="save.isPending.value" data-testid="check-save" @click="submit" />
    </template>
  </DesignerDialog>
</template>
