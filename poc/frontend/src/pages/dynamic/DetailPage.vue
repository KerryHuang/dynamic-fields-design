<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useRecordApproval } from 'src/application/dynamic/queries/approval-queries';
import { useDeleteRecord, useRecord } from 'src/application/dynamic/queries/records-queries';
import { useModuleSchema, useRecordScopedSchema } from 'src/application/dynamic/queries/schema-queries';
import { hasApprovalActions } from 'src/presentation/dynamic/approval/approval-actions';
import ApprovalHistoryOnly from 'src/presentation/dynamic/approval/ApprovalHistoryOnly.vue';
import ApprovalPanel from 'src/presentation/dynamic/approval/ApprovalPanel.vue';
import ApprovalBadge from 'src/presentation/dynamic/approval/ApprovalBadge.vue';
import SubmitApprovalDialog from 'src/presentation/dynamic/approval/SubmitApprovalDialog.vue';
import PrintMenu from 'src/presentation/dynamic/print/PrintMenu.vue';
import RecordConversions from 'src/presentation/dynamic/conversions/RecordConversions.vue';
import RecordEffects from 'src/presentation/dynamic/events/RecordEffects.vue';
import RecordLedger from 'src/presentation/dynamic/ledger/RecordLedger.vue';
import DynamicDetail from 'src/presentation/dynamic/components/DynamicDetail.vue';
import DetailTables from 'src/presentation/dynamic/details/DetailTables.vue';
import DeleteBlockedDialog from 'src/presentation/dynamic/related/DeleteBlockedDialog.vue';
import RecordHistory from 'src/presentation/dynamic/history/RecordHistory.vue';
import RecordTasks from 'src/presentation/dynamic/tasks/RecordTasks.vue';
import RelatedTabs from 'src/presentation/dynamic/related/RelatedTabs.vue';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { SubmitApprovalResult } from 'src/domain/dynamic/approval-types';
import type { DeleteBlocked } from 'src/domain/dynamic/types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { canAdvance, isReadOnlyState } from 'src/domain/dynamic/workflow';
import { isStale } from 'src/presentation/dynamic/designer/api-error';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const moduleKey = computed(() => String(route.params.moduleKey));
const recordId = computed(() => Number(route.params.id));

const schemaQuery = useModuleSchema(moduleKey);
// An approver without read access gets the schema through the temporary right of the open task; only asked after the plain one is refused.
const grantSchemaQuery = useRecordScopedSchema(moduleKey, recordId, computed(() => isForbidden(schemaQuery.error.value)));
const schema = computed(() => schemaQuery.data.value ?? grantSchemaQuery.data.value);
// While the plain schema is refused, the record-scoped request decides what went wrong.
const schemaError = computed(() => (isForbidden(schemaQuery.error.value) ? grantSchemaQuery.error.value : schemaQuery.error.value));
const recordQuery = useRecord(recordId);
const del = useDeleteRecord();
const confirming = ref(false);
const blocked = ref<DeleteBlocked | null>(null);
// Kept across a failed attempt so a resend takes effect once; replaced after a success or a 422 (key used for other content).
let deleteKey = newIdempotencyKey();

const status = (e: unknown) => (e as ApiError | null)?.status;
const schemaMissing = computed(() => status(schemaError.value) === 404);
const recordMissing = computed(() => status(recordQuery.error.value) === 404
  || (!!recordQuery.data.value && recordQuery.data.value.moduleKey !== moduleKey.value));
// The record is out of reach (the temporary right ended with the task, or it never was there): the approval history may still be.
const recordGone = computed(() => [403, 404].includes(status(recordQuery.error.value) ?? 0));
// A failed refetch keeps the last good data: only a successful answer may be shown as history (else the stale timeline outlives the task).
const historyOnly = computed(() => recordGone.value && approvalQuery.isSuccess.value
  && !!(approvalQuery.data.value?.current || approvalQuery.data.value?.previous.length));
const historyPending = computed(() => recordGone.value && approvalQuery.isPending.value);
const forbidden = computed(() => isForbidden(schemaError.value) || isForbidden(recordQuery.error.value));
const failed = computed(() => !forbidden.value && !schemaMissing.value && !recordMissing.value
  && (!!schemaError.value || !!recordQuery.error.value));

// A read-only workflow state locks the record: no delete, and edit only while the user can still move the status.
// A record an event has already posted is locked the same way (only the status may change: that is the cancel flow).
const workflow = computed(() => schema.value?.workflow);
const currentState = computed(() => (workflow.value ? recordQuery.data.value?.values[workflow.value.statusFieldKey] : undefined));
const frozen = computed(() => !!recordQuery.data.value?.frozenByEffects);
const stateLocked = computed(() => isReadOnlyState(workflow.value, currentState.value) || frozen.value);
// While an approval is running the record is read-only (the server answers 409 to PUT and DELETE); withdraw first.
const approval = computed(() => recordQuery.data.value?.approval ?? null);
const approvalPending = computed(() => approval.value?.status === 'pending');
const grant = computed(() => !!recordQuery.data.value?.approvalGrant);
const canEdit = computed(() => !grant.value && !!schema.value?.permissions.edit && !approvalPending.value && (!stateLocked.value || canAdvance(workflow.value, currentState.value)));
const canDelete = computed(() => !grant.value && !!schema.value?.permissions.delete && !stateLocked.value && !approvalPending.value);

// No summary = the module has no approval for this record: no badge, no request.
const canSubmit = computed(() => !grant.value && !!approval.value?.canSubmit && !schema.value?.readOnly);
// The block shows whenever the record has an approval summary (also after the last stage); the submit buttons need canSubmit.
const approvalQuery = useRecordApproval(recordId, () => !!approval.value || recordGone.value);
const statusField = computed(() => schema.value?.blocks.flatMap((b) => b.fields).find((f) => f.key === workflow.value?.statusFieldKey));
const stateLabel = (optionId: number) => resolveI18n(statusField.value?.options.find((o) => o.value === `opt_${optionId}`)?.label, locale.value);
const submitTo = ref<number | null>(null);
function onSubmitted(outcome: SubmitApprovalResult['outcome']) {
  const state = submitTo.value === null ? '' : stateLabel(submitTo.value);
  $q.notify({ type: 'positive', message: t(`approval.submit.outcome.${outcome}`, { state }) });
}
// On a phone the action bar is fixed to the bottom: leave room so the last block is not covered.
const identity = useIdentityStore();
const barPad = computed(() => $q.screen.lt.sm && !schema.value?.readOnly && hasApprovalActions(approvalQuery.data.value?.current ?? null, identity.userId));
const submitTargets = computed(() => (canSubmit.value ? approvalQuery.data.value?.submitTargets ?? [] : []).filter((to) => stateLabel(to)));

async function onDelete() {
  try {
    await del.mutateAsync({ moduleKey: moduleKey.value, id: recordId.value, rowVersion: recordQuery.data.value!.rowVersion, idempotencyKey: deleteKey });
    deleteKey = newIdempotencyKey();
    await router.push(`/dynamic/${moduleKey.value}`);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 422) deleteKey = newIdempotencyKey();
    if (err.status === 409 && err.references) {
      blocked.value = { message: err.message ?? '', references: err.references, total: err.total ?? err.references.length };
    } else if (err.status === 409 && err.message && !isStale(err)) {
      $q.notify({ type: 'warning', message: err.message });
    } else if (err.status === 409) {
      $q.notify({
        type: 'warning', message: t('dynamic.stale'),
        actions: [{ label: t('dynamic.reload'), color: 'white', handler: () => void recordQuery.refetch() }],
      });
    } else $q.notify({ type: 'negative', message: err.message ?? String(err.status) });
  } finally {
    confirming.value = false;
  }
}
</script>

<template>
  <q-page padding :style="barPad ? 'padding-bottom: calc(88px + env(safe-area-inset-bottom))' : undefined">
    <template v-if="historyPending" />
    <ApprovalHistoryOnly v-else-if="historyOnly" :approval="approvalQuery.data.value!" :record-id="recordId" />
    <ForbiddenNotice v-else-if="forbidden" />
    <div v-else-if="schemaMissing" class="text-negative">{{ t('dynamic.moduleNotFound') }}</div>
    <div v-else-if="recordMissing" class="text-negative">{{ t('dynamic.recordNotFound') }}</div>
    <div v-else-if="failed" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <template v-else-if="schema && recordQuery.data.value">
      <div class="row items-center q-mb-md q-gutter-sm">
        <q-btn flat icon="arrow_back" :label="t('dynamic.back')" :to="`/dynamic/${moduleKey}`" />
        <div v-if="schema.titleFieldKey || schema.kind === 'extension'" class="text-h6">{{ recordQuery.data.value.title }}</div>
        <ApprovalBadge v-if="approval" :summary="approval" />
        <q-space />
        <q-btn
          v-for="to in submitTargets" :key="to" color="primary" icon="rule" :label="t('approval.submitTo', { state: stateLabel(to) })"
          :data-testid="`submit-approval-${to}`" @click="submitTo = to"
        />
        <PrintMenu v-if="!grant" :module-key="moduleKey" :record-id="recordId" />
        <q-btn v-if="canEdit" color="primary" :label="t('dynamic.edit')" :to="`/dynamic/${moduleKey}/${recordId}/edit`" />
        <q-btn v-if="canDelete" color="negative" outline :label="t('dynamic.delete')" @click="confirming = true" />
      </div>
      <div v-if="grant" class="row items-center no-wrap q-mb-sm text-muted" data-testid="approval-grant-banner">
        <q-icon name="visibility" class="q-mr-sm" />
        <span>{{ t('approval.grantBanner') }}</span>
      </div>
      <div v-if="schema.readOnly" class="row items-center no-wrap q-mb-sm text-muted" data-testid="closed-banner">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('dynamic.closedBanner') }}</span>
      </div>
      <div v-if="approvalPending" class="row items-center no-wrap q-mb-sm text-muted" data-testid="approval-pending-readonly">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('approval.pendingReadOnly') }}</span>
      </div>
      <div v-if="frozen" class="row items-center no-wrap q-mb-sm text-muted" data-testid="frozen-notice">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('ledger.frozen') }}</span>
      </div>
      <RecordLedger v-if="schema.ledger && !grant" :record-id="recordId" :module-key="moduleKey" />
      <RecordConversions v-if="!grant" :record-id="recordId" :row-version="recordQuery.data.value.rowVersion" :closed="schema.readOnly" />
      <ApprovalPanel
        v-if="approval && approvalQuery.data.value" :approval="approvalQuery.data.value" :schema="schema" :record="recordQuery.data.value" :record-id="recordId" :module-key="moduleKey"
        :readonly="!!schema.readOnly"
      />
      <DynamicDetail :schema="schema" :record="recordQuery.data.value" />
      <DetailTables v-if="schema.details?.length" class="q-mt-md" :details="schema.details" :rows="recordQuery.data.value.details ?? {}" />
      <RecordTasks v-if="schema.tasksEnabled !== false && !grant" :record-id="recordId" :closed="!!schema.readOnly" />
      <template v-if="!grant">
        <RecordEffects :record-id="recordId" />
        <RelatedTabs :record-id="recordId" />
        <RecordHistory :record-id="recordId" />
      </template>
      <SubmitApprovalDialog
        v-if="submitTo !== null" :model-value="true" :record-id="recordId" :module-key="moduleKey" :to="submitTo" :to-label="stateLabel(submitTo)"
        :record-row-version="recordQuery.data.value.rowVersion" @update:model-value="submitTo = null" @submitted="onSubmitted"
      />
      <DeleteBlockedDialog v-if="blocked" :model-value="true" :blocked="blocked" @update:model-value="blocked = null" />
      <q-dialog v-model="confirming">
        <q-card>
          <q-card-section>{{ t('dynamic.confirmDelete') }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('dynamic.cancel')" v-close-popup />
            <q-btn color="negative" :label="t('dynamic.delete')" :loading="del.isPending.value" @click="onDelete" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
