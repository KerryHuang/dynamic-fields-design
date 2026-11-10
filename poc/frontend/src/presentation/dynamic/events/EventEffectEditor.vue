<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { joinList } from 'src/i18n/translate';
import { useModuleDetail } from 'src/application/dynamic/queries/modules-queries';
import { useHiddenFieldIds } from 'src/application/dynamic/queries/events-queries';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { emptyEffect, emptySet, type EffectDraft, type SetDraft } from 'src/domain/dynamic/event-draft';
import type { EffectErrors } from 'src/domain/dynamic/event-draft';
import type { ModuleEvent } from 'src/domain/dynamic/events-ledger';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import { copySource, fieldOptions } from 'src/presentation/dynamic/designer/field-options';
import type { ModuleSummary } from 'src/services/api/dynamic-api';
import EventSetRow from './EventSetRow.vue';

type Option = { value: string; label: string };
/** The system fills these in, so a mapping cannot write them. */
const COMPUTED_TYPES = ['formula', 'rollup', 'autonumber'];

const props = defineProps<{
  modelValue: EffectDraft;
  index: number;
  sourceModule: ModuleDetail;
  modules: ModuleSummary[];
  /** Saved events of the same source module: what a reverse effect can undo. */
  events: ModuleEvent[];
  hiddenHeader: Set<number>;
  errors?: EffectErrors;
  disabled?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [effect: EffectDraft]; remove: [] }>();
const { t, locale } = useI18n();
const setError = (i: number) => { const m = props.errors?.set.get(i); return m && joinList(m); };

const e = computed(() => props.modelValue);
const id = (suffix: string) => `effect-${props.index}-${suffix}`;
const patch = (p: Partial<EffectDraft>) => emit('update:modelValue', { ...e.value, ...p });
/** Everything below the kind and the target was chosen against the old ones. */
const cleared = (): Partial<EffectDraft> => ({ each: null, via: null, set: [], ofEvent: null, ofEffect: null });

// ---- modules ----
const nameOf = (m: ModuleSummary) => `${resolveI18n(m.name, locale.value)} (${m.key})`;
const targetOptions = computed<Option[]>(() => props.modules
  .filter((m) => m.status === 'Active' && m.kind !== 'extension' && !m.detailOf).map((m) => ({ value: m.key, label: nameOf(m) })));
const eachOptions = computed<Option[]>(() => props.modules
  .filter((m) => m.status === 'Active' && m.detailOf === props.sourceModule.key).map((m) => ({ value: m.key, label: nameOf(m) })));
const kindOptions = computed(() => (['create', 'update', 'reverse'] as const).map((value) => ({ value, label: t(`events.kind.${value}`) })));

const target = useModuleDetail(computed(() => e.value.targetModuleKey ?? ''));
const eachModule = useModuleDetail(computed(() => (e.value.kind === 'create' ? e.value.each ?? '' : '')));
const hiddenLine = useHiddenFieldIds(computed(() => (e.value.kind === 'create' ? e.value.each ?? '' : '')));

// ---- field options ----
const viaOptions = computed(() => fieldOptions(props.sourceModule, locale.value,
  (f) => f.type === 'reference' && f.config.refTarget?.kind === 'module' && f.config.refTarget.key === e.value.targetModuleKey));
const toOptions = computed(() => fieldOptions(target.data.value, locale.value, (f) => !COMPUTED_TYPES.includes(f.type)));
const headerOptions = computed(() => fieldOptions(props.sourceModule, locale.value, copySource));
const lineOptions = computed(() => fieldOptions(eachModule.data.value as ModuleDetail | undefined, locale.value, copySource));
const targetFieldOf = (fieldId: number | null) => target.data.value?.blocks.flatMap((b) => b.fields).find((f) => f.id === fieldId);

const eventOptions = computed<Option[]>(() => props.events.map((ev) => ({ value: ev.key, label: `${resolveI18n(ev.name, locale.value)} (${ev.key})` })));
const effectOptions = computed<Option[]>(() => (props.events.find((ev) => ev.key === e.value.ofEvent)?.definition.effects ?? [])
  .filter((x) => x.kind === 'create' && x.targetModuleKey === e.value.targetModuleKey).map((x) => ({ value: x.code, label: x.code })));

const kindSel = useSelectFilter(kindOptions);
const targetSel = useSelectFilter(targetOptions);
const eachSel = useSelectFilter(eachOptions);
const viaSel = useSelectFilter(viaOptions);
const eventSel = useSelectFilter(eventOptions);
const effectSel = useSelectFilter(effectOptions);

// ---- mapping rows ----
const setRow = (i: number, row: SetDraft) => patch({ set: e.value.set.map((s, j) => (j === i ? row : s)) });
const addRow = () => patch({ set: [...e.value.set, emptySet()] });
const removeRow = (i: number) => patch({ set: e.value.set.filter((_, j) => j !== i) });
const hasMapping = computed(() => e.value.kind === 'create' || e.value.kind === 'update');
const argsText = computed(() => (e.value.custom?.args === undefined ? '' : JSON.stringify(e.value.custom.args, null, 2)));
</script>

<template>
  <q-card flat bordered class="q-pa-md" :data-testid="`effect-${index}`">
    <div class="row items-center no-wrap q-mb-sm">
      <div class="text-subtitle2 ellipsis">#{{ index + 1 }} {{ e.code }}</div>
      <q-space />
      <q-btn flat dense round icon="delete" color="negative" :title="t('events.removeEffect')" :disable="disabled" :data-testid="id('remove')" @click="emit('remove')" />
    </div>

    <div v-if="e.custom" class="column q-gutter-y-xs" :data-testid="id('custom')">
      <div class="text-muted">{{ t('events.customReadonly') }}</div>
      <div>{{ t('events.kind.custom') }} · {{ t('events.handler') }}：{{ e.custom.handler }}</div>
      <pre v-if="argsText" class="effect-args">{{ argsText }}</pre>
    </div>

    <div v-else class="column q-gutter-y-md">
      <div class="row q-col-gutter-sm items-start">
        <div class="col-12 col-sm-5">
          <q-input
            :model-value="e.code" :label="t('events.effectCode')" :hint="e.saved ? t('events.effectCodeHint') : undefined" :readonly="e.saved" :disable="disabled"
            outlined dense :data-testid="id('code')" @update:model-value="(v) => patch({ code: String(v ?? '') })"
          />
        </div>
        <div class="col-12 col-sm-7">
          <q-select
            :model-value="e.kind" :options="kindSel.filtered.value" :label="t('events.kindLabel')" :disable="disabled"
            emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('kind')"
            @filter="kindSel.onFilter" @popup-hide="kindSel.reset"
            @update:model-value="(v: EffectDraft['kind']) => emit('update:modelValue', { ...emptyEffect(e.code), kind: v, saved: e.saved })"
          />
        </div>
      </div>

      <q-select
        :model-value="e.targetModuleKey" :options="targetSel.filtered.value" :label="t('events.target')" :disable="disabled"
        emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('target')"
        @filter="targetSel.onFilter" @popup-hide="targetSel.reset" @update:model-value="(v: string) => patch({ targetModuleKey: v, ...cleared() })"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>

      <q-select
        v-if="e.kind === 'create'" :model-value="e.each" :options="eachSel.filtered.value" :label="t('events.each')"
        :hint="eachOptions.length ? t('events.eachHint') : undefined" :disable="disabled" clearable
        emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('each')"
        @filter="eachSel.onFilter" @popup-hide="eachSel.reset" @update:model-value="(v: string | null) => patch({ each: v, set: [] })"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>

      <q-select
        v-if="e.kind === 'update'" :model-value="e.via" :options="viaSel.filtered.value" :label="t('events.via')" :hint="t('events.viaHint')" :disable="disabled"
        emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('via')"
        @filter="viaSel.onFilter" @popup-hide="viaSel.reset" @update:model-value="(v: number) => patch({ via: v })"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>

      <div v-if="e.kind === 'reverse'" class="row q-col-gutter-sm items-start">
        <div class="col-12 col-sm-6">
          <q-select
            :model-value="e.ofEvent" :options="eventSel.filtered.value" :label="t('events.ofEvent')" :disable="disabled"
            emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('of-event')"
            @filter="eventSel.onFilter" @popup-hide="eventSel.reset" @update:model-value="(v: string) => patch({ ofEvent: v, ofEffect: null })"
          >
            <template #no-option><SelectNoOption /></template>
          </q-select>
        </div>
        <div class="col-12 col-sm-6">
          <q-select
            :model-value="e.ofEffect" :options="effectSel.filtered.value" :label="t('events.ofEffect')" :disable="disabled || !e.ofEvent"
            emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="id('of-effect')"
            @filter="effectSel.onFilter" @popup-hide="effectSel.reset" @update:model-value="(v: string) => patch({ ofEffect: v })"
          >
            <template #no-option><SelectNoOption /></template>
          </q-select>
        </div>
      </div>

      <div v-if="hasMapping" class="column q-gutter-y-sm" :data-testid="id('set')">
        <div class="row items-center">
          <div class="text-subtitle2">{{ t('events.set') }}</div>
          <q-space />
          <q-btn dense flat color="primary" icon="add" :label="t('events.addSet')" :disable="disabled || !e.targetModuleKey" :data-testid="id('set-add')" @click="addRow" />
        </div>
        <div v-if="!e.set.length" class="text-caption text-muted">{{ t('events.noSet') }}</div>
        <EventSetRow
          v-for="(row, i) in e.set" :key="i" :model-value="row" :id-prefix="`set-${index}-${i}`" :kind="e.kind as 'create' | 'update'"
          :to-options="toOptions" :target-field="targetFieldOf(row.to)" :header-options="headerOptions" :line-options="lineOptions"
          :has-line="e.kind === 'create' && !!e.each" :hidden-header="hiddenHeader" :hidden-line="hiddenLine" :disabled="disabled"
          :error="setError(i)" @update:model-value="(r) => setRow(i, r)" @remove="removeRow(i)"
        />
      </div>
    </div>

    <div v-if="errors?.own.length" class="text-negative q-mt-sm" :data-testid="id('errors')">
      <div v-for="line in errors.own" :key="line">{{ line }}</div>
    </div>
  </q-card>
</template>

<style scoped>
.effect-args { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
