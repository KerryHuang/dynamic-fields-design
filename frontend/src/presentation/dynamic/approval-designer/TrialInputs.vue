<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useSearchTrialRecords } from 'src/application/dynamic/queries/approval-flow-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DynamicRecord, FilterOperator, ModuleDetail, SchemaField } from 'src/domain/dynamic/types';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import type { ApiError } from 'src/services/api/http';

// 試算的輸入：發起人、參考記錄、欄位值。舊的流程對話框（FlowTrialPanel）與流程圖設計頁（DesignerTrialPanel）共用；
// 狀態留在這裡，外層用 ref 讀 `initiator`、`recordId`、`sentValues`，任何一項改動發 `change`（外層據此清掉過期的結果）。
const props = defineProps<{ module: ModuleDetail; fieldIds: number[] }>();
const emit = defineEmits<{ change: [] }>();
const { t, locale } = useI18n();

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
  const ids = new Set(props.fieldIds);
  return schemaFields.value.filter((f) => ids.has(f.id) && fieldTypeRegistry[f.type].filterInput(f, 'eq' as FilterOperator));
});
const values = ref<Record<string, unknown>>({});
const setValue = (f: SchemaField, v: unknown) => { values.value = { ...values.value, [fieldKey(f)]: v ?? null }; };

let pickedRecord: DynamicRecord | undefined;
/** Prefill the rows the record has a value for and the user has not touched (a null the user typed stays). */
function prefill(into: Record<string, unknown>) {
  if (!pickedRecord) return;
  for (const f of trialFields.value) {
    const k = fieldKey(f);
    if (!(k in into) && pickedRecord.values[k] !== undefined) into[k] = pickedRecord.values[k];
  }
}
function pickRecord(id: number | null) {
  recordId.value = id;
  pickedRecord = recordOptions.value.find((o) => o.value === id)?.record;
  const next: Record<string, unknown> = {};
  prefill(next);
  values.value = next;
}
// An edit that makes the draft refer to more fields adds their rows, prefilled from the chosen record.
watch(trialFields, () => {
  const next = { ...values.value };
  prefill(next);
  if (Object.keys(next).length !== Object.keys(values.value).length) values.value = next;
});
watch([initiator, recordId, values], () => emit('change'));

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

/** What is sent: only the rows still shown (a value of a field the draft no longer refers to is kept but not sent). */
const sentValues = computed(() => {
  const keys = new Set(trialFields.value.map(fieldKey));
  const sent = Object.fromEntries(Object.entries(values.value).filter(([k]) => keys.has(k)));
  return Object.keys(sent).length ? sent : undefined;
});
defineExpose({ initiator, recordId, sentValues });
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="trial-inputs">
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
  </div>
</template>
