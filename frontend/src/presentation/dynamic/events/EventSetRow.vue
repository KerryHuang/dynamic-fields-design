<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { usesHiddenSource, type SetDraft } from 'src/domain/dynamic/event-draft';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DesignerField } from 'src/domain/dynamic/types';

type Option = { value: number; label: string };

const props = defineProps<{
  modelValue: SetDraft;
  /** Prefix of the data-testid attributes, e.g. `set-0-1` (effect 0, mapping 1). */
  idPrefix: string;
  kind: 'create' | 'update';
  toOptions: Option[];
  /** The target field picked in `to`; decides what kind of constant can be typed. */
  targetField?: DesignerField;
  headerOptions: Option[];
  lineOptions: Option[];
  /** The effect expands detail rows, so values can also come from the detail row. */
  hasLine: boolean;
  hiddenHeader: Set<number>;
  hiddenLine: Set<number>;
  error?: string;
  disabled?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [row: SetDraft]; remove: [] }>();
const { t, locale } = useI18n();

const id = (suffix: string) => `${props.idPrefix}-${suffix}`;
/** Changing where the value comes from withdraws the hidden-value consent: it was given for the old source. */
const change = (p: Partial<SetDraft>) => emit('update:modelValue', { ...props.modelValue, allowHiddenSource: false, ...p });

const row = computed(() => props.modelValue);
const sourceOptions = computed(() => (['from', 'const', 'formula'] as const).map((value) => ({ value, label: t(`events.source.${value}`) })));
const sideOptions = computed(() => (['header', 'line'] as const).map((value) => ({ value, label: t(value === 'header' ? 'events.sideHeader' : 'events.sideLine') })));
const side = computed(() => (props.hasLine ? row.value.side : 'header'));
const fieldOptions = computed(() => (side.value === 'line' ? props.lineOptions : props.headerOptions));
const needsConsent = computed(() => usesHiddenSource({ ...row.value, side: side.value }, side.value === 'line' ? props.hiddenLine : props.hiddenHeader));

const toSel = useSelectFilter(() => props.toOptions);
const fieldSel = useSelectFilter(fieldOptions);
const insertSel = useSelectFilter(fieldOptions);
const optionSel = useSelectFilter(() => constOptions.value);

function setSource(source: SetDraft['source']) {
  change({ source, side: 'header', fieldId: null, expr: '', value: null });
}
function insertField(fieldId: number) {
  change({ expr: `${row.value.expr}{${fieldId}}` });
}

// ---- constant: the input follows the target field's type ----
const type = computed(() => props.targetField?.type);
const isOption = computed(() => type.value === 'enum' || type.value === 'multienum');
const isNumber = computed(() => ['integer', 'decimal', 'currency', 'percent'].includes(type.value ?? ''));
const isDate = computed(() => type.value === 'date');
const constOptions = computed(() => (props.targetField?.options ?? []).filter((o) => o.active)
  .map((o) => ({ value: o.value, label: resolveI18n(o.label, locale.value) })));
const today = computed(() => row.value.value === 'today');
const setNumber = (v: string | number | null) => change({ value: v === '' || v === null ? null : Number(v) });
</script>

<template>
  <q-card flat bordered class="q-pa-sm" :data-testid="idPrefix">
    <div class="row q-col-gutter-sm items-start">
      <div class="col-12 col-sm-4">
        <q-select
          :model-value="row.to" :options="toSel.filtered.value" :label="t('events.setTo')" :disable="disabled"
          emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('to')"
          @filter="toSel.onFilter" @popup-hide="toSel.reset" @update:model-value="(v: number) => emit('update:modelValue', { ...row, to: v })"
        >
          <template #no-option><SelectNoOption /></template>
        </q-select>
      </div>
      <div class="col-12 col-sm-3">
        <q-select
          :model-value="row.source" :options="sourceOptions" :label="t('events.setSource')" :disable="disabled"
          emit-value map-options outlined dense :data-testid="id('source')" @update:model-value="setSource"
        />
      </div>
      <div class="col-12 col-sm-5">
        <div class="row q-col-gutter-sm items-start">
          <div v-if="row.source !== 'const' && hasLine" class="col-12 col-sm-5">
            <q-select
              :model-value="row.side" :options="sideOptions" :label="t('events.side')" :disable="disabled"
              emit-value map-options outlined dense :data-testid="id('side')"
              @update:model-value="(v: 'header' | 'line') => change({ side: v, fieldId: null, expr: '' })"
            />
          </div>
          <div v-if="row.source === 'from'" class="col-12" :class="hasLine ? 'col-sm-7' : ''">
            <q-select
              :model-value="row.fieldId" :options="fieldSel.filtered.value" :label="t('events.sourceField')" :disable="disabled"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('field')"
              @filter="fieldSel.onFilter" @popup-hide="fieldSel.reset" @update:model-value="(v: number) => change({ fieldId: v })"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div v-else-if="row.source === 'formula'" class="col-12" :class="hasLine ? 'col-sm-7' : ''">
            <q-select
              :model-value="null" :options="insertSel.filtered.value" :label="t('events.insertField')" :disable="disabled"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('insert')"
              @filter="insertSel.onFilter" @popup-hide="insertSel.reset" @update:model-value="insertField"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div v-else class="col-12">
            <q-select
              v-if="isOption" :model-value="row.value" :options="optionSel.filtered.value" :label="t('events.value')" :disable="disabled"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('const')"
              @filter="optionSel.onFilter" @popup-hide="optionSel.reset" @update:model-value="(v: string) => change({ value: v })"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
            <q-toggle
              v-else-if="type === 'boolean'" :model-value="row.value === true" :label="t('events.value')" :disable="disabled"
              :data-testid="id('const')" @update:model-value="(v: boolean) => change({ value: v })"
            />
            <template v-else-if="isDate">
              <q-checkbox
                :model-value="today" dense :label="t('events.today')" :disable="disabled" :data-testid="id('today')"
                @update:model-value="(v: boolean) => change({ value: v ? 'today' : null })"
              />
              <q-input
                v-if="!today" :model-value="row.value as string" type="date" :label="t('events.value')" :disable="disabled" outlined dense
                :data-testid="id('const')" @update:model-value="(v) => change({ value: v })"
              />
            </template>
            <q-input
              v-else-if="isNumber" :model-value="row.value as number" type="number" step="any" :label="t('events.value')" :disable="disabled"
              outlined dense :data-testid="id('const')" @update:model-value="setNumber"
            />
            <q-input
              v-else :model-value="row.value as string" :label="t('events.value')" :disable="disabled" outlined dense
              :data-testid="id('const')" @update:model-value="(v) => change({ value: v })"
            />
          </div>
        </div>
      </div>
    </div>

    <div v-if="row.source === 'formula'" class="q-mt-sm">
      <q-input
        :model-value="row.expr" :label="t('events.expr')" :hint="t('events.exprHint')" :disable="disabled" outlined dense
        :data-testid="id('expr')" @update:model-value="(v) => change({ expr: String(v ?? '') })"
      />
    </div>

    <div v-if="needsConsent" class="q-mt-sm" :data-testid="id('consent')">
      <q-checkbox
        :model-value="row.allowHiddenSource" dense :label="t('events.allowHidden')" :disable="disabled" :data-testid="id('allow-hidden')"
        @update:model-value="(v: boolean) => emit('update:modelValue', { ...row, allowHiddenSource: v })"
      />
      <div class="text-caption text-muted">{{ t('events.allowHiddenHint') }}</div>
    </div>

    <div v-if="error" class="text-negative text-caption q-mt-xs" :data-testid="id('error')">{{ error }}</div>
    <div class="row justify-end q-mt-xs">
      <q-btn flat dense round icon="delete" color="negative" :title="t('events.removeSet')" :disable="disabled" :data-testid="id('remove')" @click="emit('remove')" />
    </div>
  </q-card>
</template>
