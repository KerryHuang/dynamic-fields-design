<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { usePreviewApprovalDraft, useSearchTrialRecords } from 'src/application/dynamic/queries/approval-flow-queries';
import { buildFlowInput, type FlowDraft } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow } from 'src/domain/dynamic/approval-flow-types';
import type { ApprovalPreview } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DynamicRecord, FilterOperator, ModuleDetail, SchemaField } from 'src/domain/dynamic/types';
import PreviewStages from 'src/presentation/dynamic/approval/PreviewStages.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import type { ApiError } from 'src/services/api/http';
import { trialFieldIds } from './trial-fields';

const props = defineProps<{ module: ModuleDetail; draft: FlowDraft; flows: ApprovalFlow[] }>();
const { t, locale } = useI18n();

// What the trial sends: the draft as it is being edited, always as active (the backend treats it so too).
const input = computed(() => buildFlowInput({ ...props.draft, active: true }));

// ---- initiator ----
const users = useUsers();
const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName })));
const initiator = ref<string | null>(null);

// ---- record (optional) ----
const search = useSearchTrialRecords(() => props.module.key);
const recordOptions = ref<{ value: number; label: string; record: DynamicRecord }[]>([]);
const recordId = ref<number | null>(null);
const recordNote = ref(false);
function filterRecords(val: string, update: (fn: () => void) => void) {
  search.mutate(val, {
    onSuccess: (r) => {
      recordNote.value = false;
      update(() => { recordOptions.value = r.items.map((rec) => ({ value: rec.id, label: rec.title || `#${rec.id}`, record: rec })); });
    },
    onError: (e) => {
      const s = (e as unknown as ApiError).status;
      recordNote.value = s === 403 || s === 404;
      update(() => { recordOptions.value = []; });
    },
  });
}

// ---- field values ----
const schemaFields = computed(() => toSchemaFields(props.module));
const fieldKey = (f: SchemaField) => `f_${f.id}`;
const trialFields = computed(() => {
  const ids = input.value ? new Set(trialFieldIds(props.flows, input.value)) : new Set<number>();
  return schemaFields.value.filter((f) => ids.has(f.id) && fieldTypeRegistry[f.type].filterInput(f, 'eq' as FilterOperator));
});
const values = ref<Record<string, unknown>>({});
const setValue = (f: SchemaField, v: unknown) => { values.value = { ...values.value, [fieldKey(f)]: v ?? null }; };

function pickRecord(id: number | null) {
  recordId.value = id;
  const rec = recordOptions.value.find((o) => o.value === id)?.record;
  const next: Record<string, unknown> = {};
  if (rec) for (const f of trialFields.value) if (rec.values[fieldKey(f)] !== undefined) next[fieldKey(f)] = rec.values[fieldKey(f)];
  values.value = next;
}

/** Value input descriptor with the current locale applied over the zh-TW labels from the registry (same as the condition editor). */
function inputOf(f: SchemaField) {
  const d = fieldTypeRegistry[f.type].filterInput(f, 'eq' as FilterOperator);
  if (!d) return undefined;
  const p: Record<string, unknown> = { ...d.props, label: resolveI18n(f.label, locale.value) };
  if (Array.isArray(p.options)) {
    p.options = (p.options as { value: string; label: string }[]).map((o) => {
      const opt = f.options.find((x) => x.value === o.value);
      return opt ? { ...o, label: resolveI18n(opt.label, locale.value) } : o;
    });
  }
  return { component: d.component, props: p };
}

// ---- run ----
const preview = usePreviewApprovalDraft(() => props.module.key);
const result = ref<ApprovalPreview | null>(null);
const errors = ref<string[]>([]);
const failed = ref(false);
// An edit makes the shown result stale.
watch(() => [JSON.stringify(input.value), initiator.value, recordId.value, values.value], () => { result.value = null; errors.value = []; failed.value = false; });

function run() {
  if (!input.value || !initiator.value) return;
  result.value = null; errors.value = []; failed.value = false;
  const keys = new Set(trialFields.value.map(fieldKey));
  const sent = Object.fromEntries(Object.entries(values.value).filter(([k]) => keys.has(k)));
  preview.mutate({
    draft: input.value, initiatorUserId: initiator.value, recordId: recordId.value ?? undefined, values: Object.keys(sent).length ? sent : undefined,
  }, {
    onSuccess: (r) => { result.value = r; },
    onError: (e) => {
      const err = e as unknown as ApiError;
      const msgs = Object.values(err.errors ?? {}).flat();
      if (err.status === 400 && msgs.length) errors.value = msgs;
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
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="flow-trial">
    <div v-if="!input" class="text-muted" data-testid="trial-need-target">{{ t('approval.designer.trial.needTarget') }}</div>
    <template v-else>
      <q-select
        v-model="initiator" :options="userOptions" :label="t('approval.designer.trial.initiator')" emit-value map-options dense outlined
        data-testid="trial-initiator"
      />
      <div>
        <q-select
          :model-value="recordId" :options="recordOptions" :label="t('approval.designer.trial.record')" emit-value map-options dense outlined clearable
          use-input input-debounce="300" :hint="t('approval.designer.trial.recordHint')" data-testid="trial-record"
          @filter="filterRecords" @update:model-value="pickRecord"
        >
          <template #no-option><q-item><q-item-section class="text-muted">{{ t('approval.designer.trial.recordNoMatch') }}</q-item-section></q-item></template>
        </q-select>
        <div v-if="recordNote" class="text-caption text-warning q-mt-xs" data-testid="trial-record-forbidden">{{ t('approval.designer.trial.recordForbidden') }}</div>
      </div>
      <div class="column no-wrap q-gutter-y-sm" data-testid="trial-values">
        <div class="text-subtitle2">{{ t('approval.designer.trial.values') }}</div>
        <div v-if="!trialFields.length" class="text-muted text-caption" data-testid="trial-no-fields">{{ t('approval.designer.trial.noFields') }}</div>
        <template v-else>
          <div class="text-muted text-caption">{{ t('approval.designer.trial.valuesHint') }}</div>
          <template v-for="f in trialFields" :key="f.id">
            <component
              :is="inputOf(f)!.component" v-bind="inputOf(f)!.props" :model-value="values[fieldKey(f)] ?? null" :data-testid="`trial-field-${f.id}`"
              @update:model-value="(v: unknown) => setValue(f, v)"
            />
          </template>
        </template>
      </div>
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
          <PreviewStages :stages="result.stages" designer />
        </template>
      </div>
      <div class="text-caption text-muted">{{ t('approval.designer.trial.note') }}</div>
    </template>
  </div>
</template>
