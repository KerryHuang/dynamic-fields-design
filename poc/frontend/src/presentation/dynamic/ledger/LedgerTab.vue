<script setup lang="ts">
import { useQueries } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDeleteLedgerMutation, useLedgerQuery, useSaveLedgerMutation } from 'src/application/dynamic/queries/ledger-queries';
import { useDesignerMutation, useModules } from 'src/application/dynamic/queries/modules-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useWorkflowQuery } from 'src/application/dynamic/queries/workflow-queries';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import {
  buildLedgerBody, newReversalField, numberFields, reversalCandidates, swapPartners, type LedgerDraft,
} from 'src/domain/dynamic/ledger-draft';
import type { DesignerField, I18nText, Locale, ModuleDetail } from 'src/domain/dynamic/types';
import { errorFor, messagesOutside } from 'src/presentation/dynamic/designer/api-error';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

const LOCALES: Locale[] = ['zh-TW', 'en-US', 'th', 'vi'];

const props = defineProps<{ module: ModuleDetail }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const key = computed(() => props.module.key);
const ledger = useLedgerQuery(key);
const workflow = useWorkflowQuery(key);
const me = useMe();
const save = useSaveLedgerMutation(key);
const remove = useDeleteLedgerMutation(key);
const modules = useModules();

const readonly = computed(() => !me.data.value?.designer.edit);
const unsupported = computed(() => !!props.module.detailOf || props.module.kind === 'extension');
const hasWorkflow = computed(() => !!workflow.data.value);
const ready = computed(() => !ledger.isPending.value && !workflow.isPending.value && me.data.value);
const exists = computed(() => !!ledger.data.value);
const recordCount = computed(() => ledger.data.value?.recordCount ?? 0);
/** A ledger with records can never be undone (they are frozen). */
const locked = computed(() => exists.value && recordCount.value > 0);

// ---- draft ----
const draft = ref<LedgerDraft>({ enabled: false, reversalFieldId: null, negate: [], swap: [] });
const errors = ref<ApiError | null>(null);
// following the query also drops the previous module's draft
watch(() => ledger.data.value, (d) => {
  draft.value = d
    ? { enabled: true, reversalFieldId: d.reversalFieldId, negate: [...d.negate], swap: d.swap.map(([a, b]) => [a ?? null, b ?? null]) }
    : { enabled: false, reversalFieldId: null, negate: [], swap: [] };
  errors.value = null;
}, { immediate: true });
const patch = (p: Partial<LedgerDraft>) => { draft.value = { ...draft.value, ...p }; errors.value = null; };

// ---- field options: this module's fields, and the number fields of its detail modules ----
const detailKeys = computed(() => (modules.data.value ?? []).filter((m) => m.detailOf === props.module.key && m.status === 'Active').map((m) => m.key));
const detailQueries = useQueries({
  queries: computed(() => detailKeys.value.map((k) => ({ queryKey: ['module', k], queryFn: () => dynamicApi.getModule(k), retry: false }))),
});
const details = computed(() => detailQueries.value.map((q) => q.data).filter((m): m is ModuleDetail => !!m));
const ownFields = computed(() => props.module.blocks.flatMap((b) => b.fields));
/** A swap pair lives in one module, so fields are grouped by the module that owns them. */
const groups = computed(() => [
  { prefix: '', fields: ownFields.value },
  ...details.value.map((m) => ({ prefix: `${resolveI18n(m.name, locale.value)}：`, fields: m.blocks.flatMap((b) => b.fields) })),
]);
const labelOf = (prefix: string, f: DesignerField) => `${prefix}${resolveI18n(f.label, locale.value)}`;
const numberOptions = computed(() => groups.value.flatMap((g) => numberFields(g.fields).map((f) => ({ value: f.id, label: labelOf(g.prefix, f) }))));
const ownerOf = (id: number | null) => groups.value.find((g) => g.fields.some((f) => f.id === id));
const partnerOptions = (firstId: number | null) => {
  const g = ownerOf(firstId);
  return g ? swapPartners(g.fields, firstId).map((f) => ({ value: f.id, label: labelOf(g.prefix, f) })) : [];
};

const candidates = computed(() => reversalCandidates(props.module).map((f) => ({ value: f.id, label: resolveI18n(f.label, locale.value) })));
const reversalSel = useSelectFilter(candidates);
const negateSel = useSelectFilter(numberOptions);
const swapASel = useSelectFilter<{ value: number; label: string }>([]);
const swapBSel = useSelectFilter<{ value: number; label: string }>([]);

const setSwap = (i: number, p: Partial<{ 0: number | null; 1: number | null }>) =>
  patch({ swap: draft.value.swap.map((pair, j) => (j === i ? [p[0] !== undefined ? p[0] : pair[0], p[1] !== undefined ? p[1] : pair[1]] : pair)) });
const addSwap = () => patch({ swap: [...draft.value.swap, [null, null]] });
const removeSwap = (i: number) => patch({ swap: draft.value.swap.filter((_, j) => j !== i) });

// ---- create the reversal field ----
const addField = useDesignerMutation(
  () => {
    // the label carries all four languages, so the field reads right whichever one the user works in
    const label = Object.fromEntries(LOCALES.map((l) => [l, t('ledger.reversalFieldLabel', {}, { locale: l })])) as I18nText;
    return dynamicApi.addField(key.value, newReversalField(props.module.blocks[0]!.id, key.value, label));
  },
  () => key.value,
);
function createReversalField() {
  addField.mutate(undefined, {
    onSuccess: (f) => patch({ reversalFieldId: f.id }),
    onError: (e) => $q.notify({ type: 'negative', message: (e as unknown as ApiError).message ?? t('designer.saveFailed') }),
  });
}

// ---- save ----
const body = computed(() => buildLedgerBody(draft.value, false));
// A ledger with records is frozen together with its reversal rules (reversing entries must stay consistent): nothing to save.
const canSave = computed(() => !readonly.value && !locked.value && (draft.value.enabled ? !!body.value : exists.value));
const confirming = ref(false);
/** How many records get frozen, read from the server's 409 message (null when it does not say). */
const confirmCount = ref<number | null>(null);

function fail(e: unknown) {
  const err = e as ApiError;
  errors.value = err;
  if (!Object.keys(err.errors ?? {}).length) $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
}
function submit(confirmFreeze = false) {
  errors.value = null;
  if (!draft.value.enabled) {
    remove.mutate(undefined, { onSuccess: () => $q.notify({ type: 'positive', message: t('ledger.removed') }), onError: fail });
    return;
  }
  const b = buildLedgerBody(draft.value, confirmFreeze);
  if (!b) return;
  save.mutate(b, {
    onSuccess: () => { confirming.value = false; $q.notify({ type: 'positive', message: t('ledger.saved') }); },
    onError: (e) => {
      const err = e as unknown as ApiError;
      // the first time a module with records becomes a ledger, the server says how many get frozen and waits for a yes
      if (err.code === 'ledgerFreezeConfirm' && !confirmFreeze) {
        confirmCount.value = typeof err.params?.count === 'number' ? err.params.count : null;
        confirming.value = true;
      } else {
        confirming.value = false;
        fail(e);
      }
    },
  });
}

const reversalError = computed(() => errorFor(errors.value, 'reversalFieldId'));
const messages = computed(() => {
  const e = errors.value;
  if (!e) return [];
  const rest = messagesOutside(e.errors, ['reversalFieldId']);
  return rest.length ? rest : (e.status === 400 && e.message ? [e.message] : []);
});
</script>

<template>
  <div v-if="unsupported" class="text-muted" data-testid="ledger-unsupported">{{ t('ledger.unsupported') }}</div>
  <div v-else-if="ledger.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="!ready" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else class="column q-gutter-y-md" data-testid="ledger-tab">
    <div class="text-caption text-muted">{{ t('ledger.hint') }}</div>

    <div>
      <q-toggle
        :model-value="draft.enabled" :label="t('ledger.enable')" :disable="readonly || locked || hasWorkflow" data-testid="ledger-enable"
        @update:model-value="(v: boolean) => patch({ enabled: v })"
      />
      <div v-if="hasWorkflow" class="text-warning q-mt-xs" data-testid="ledger-has-workflow">{{ t('ledger.hasWorkflow') }}</div>
      <div v-else-if="locked" class="text-muted q-mt-xs" data-testid="ledger-locked">{{ t('ledger.locked', { n: recordCount }) }}</div>
    </div>

    <template v-if="draft.enabled">
      <div class="row q-col-gutter-sm items-start">
        <div class="col-12 col-sm">
          <q-select
            :model-value="draft.reversalFieldId" :options="reversalSel.filtered.value" :label="t('ledger.reversalField')" :disable="readonly || locked"
            :hint="candidates.length ? t('ledger.reversalFieldHint') : undefined" :error="!!reversalError || undefined" :error-message="reversalError"
            emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected data-testid="ledger-reversal-field"
            @filter="reversalSel.onFilter" @popup-hide="reversalSel.reset" @update:model-value="(v: number) => patch({ reversalFieldId: v })"
          >
            <template #no-option><SelectNoOption /></template>
          </q-select>
          <div v-if="!candidates.length" class="text-caption text-muted q-mt-xs" data-testid="ledger-no-candidate">{{ t('ledger.noReversalCandidate') }}</div>
        </div>
        <div class="col-12 col-sm-auto">
          <q-btn
            outline color="primary" icon="add" :label="t('ledger.createReversalField')" :disable="readonly || locked || !module.blocks.length"
            :loading="addField.isPending.value" data-testid="ledger-create-reversal" @click="createReversalField"
          />
        </div>
      </div>

      <q-select
        :model-value="draft.negate" :options="negateSel.filtered.value" :label="t('ledger.negate')" :hint="t('ledger.negateHint')" :disable="readonly || locked"
        multiple use-chips emit-value map-options outlined dense use-input input-debounce="0" data-testid="ledger-negate"
        @filter="negateSel.onFilter" @popup-hide="negateSel.reset" @update:model-value="(v: number[]) => patch({ negate: v })"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>

      <div>
        <div class="row items-center">
          <div class="text-subtitle1">{{ t('ledger.swap') }}</div>
          <q-space />
          <q-btn dense color="primary" icon="add" :label="t('ledger.addSwap')" :disable="readonly || locked" data-testid="ledger-swap-add" @click="addSwap" />
        </div>
        <div class="text-caption text-muted q-mb-xs">{{ t('ledger.swapHint') }}</div>
        <div v-if="!draft.swap.length" class="text-muted">{{ t('ledger.noSwap') }}</div>
        <div v-for="(pair, i) in draft.swap" :key="i" class="row q-col-gutter-sm items-start q-mb-xs" :data-testid="`ledger-swap-${i}`">
          <div class="col-12 col-sm">
            <q-select
              :model-value="pair[0]" :options="swapASel.filterBy(numberOptions)" :label="t('ledger.swapA')" :disable="readonly || locked"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="`ledger-swap-a-${i}`"
              @filter="swapASel.onFilter" @popup-hide="swapASel.reset" @update:model-value="(v: number) => setSwap(i, { 0: v, 1: null })"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div class="col-12 col-sm">
            <q-select
              :model-value="pair[1]" :options="swapBSel.filterBy(partnerOptions(pair[0]))" :label="t('ledger.swapB')" :disable="readonly || locked || pair[0] == null"
              emit-value map-options outlined dense use-input input-debounce="0" fill-input hide-selected :data-testid="`ledger-swap-b-${i}`"
              @filter="swapBSel.onFilter" @popup-hide="swapBSel.reset" @update:model-value="(v: number) => setSwap(i, { 1: v })"
            >
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
          <div class="col-12 col-sm-auto">
            <q-btn flat dense round icon="delete" color="negative" :title="t('ledger.removeSwap')" :disable="readonly || locked" :data-testid="`ledger-swap-remove-${i}`" @click="removeSwap(i)" />
          </div>
        </div>
      </div>
    </template>

    <div v-if="messages.length" class="text-negative" data-testid="ledger-errors">
      <div v-for="line in messages" :key="line">{{ line }}</div>
    </div>

    <div class="row justify-end">
      <q-btn
        color="primary" :label="draft.enabled ? t('ledger.save') : t('ledger.remove')" :disable="!canSave"
        :loading="save.isPending.value || remove.isPending.value" data-testid="ledger-save" @click="submit()"
      />
    </div>

    <q-dialog v-model="confirming">
      <q-card class="ledger-confirm" data-testid="ledger-confirm">
        <q-card-section class="text-h6">{{ t('ledger.confirmTitle') }}</q-card-section>
        <q-card-section>{{ confirmCount === null ? t('ledger.confirmBodyUnknown') : t('ledger.confirmBody', { n: confirmCount }) }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" data-testid="ledger-confirm-cancel" />
          <q-btn color="negative" :label="t('ledger.confirmOk')" :loading="save.isPending.value" data-testid="ledger-confirm-ok" @click="submit(true)" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.ledger-confirm { width: 440px; max-width: 95vw; }
</style>
