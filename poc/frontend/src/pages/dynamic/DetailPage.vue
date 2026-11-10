<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useDeleteRecord, useRecord } from 'src/application/dynamic/queries/records-queries';
import { useModuleSchema } from 'src/application/dynamic/queries/schema-queries';
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
import type { DeleteBlocked } from 'src/domain/dynamic/types';
import { canAdvance, isReadOnlyState } from 'src/domain/dynamic/workflow';
import { isStale } from 'src/presentation/dynamic/designer/api-error';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const moduleKey = computed(() => String(route.params.moduleKey));
const recordId = computed(() => Number(route.params.id));

const schemaQuery = useModuleSchema(moduleKey);
const recordQuery = useRecord(recordId);
const del = useDeleteRecord();
const confirming = ref(false);
const blocked = ref<DeleteBlocked | null>(null);
// Kept across a failed attempt so a resend takes effect once; replaced after a success or a 422 (key used for other content).
let deleteKey = newIdempotencyKey();

const status = (e: unknown) => (e as ApiError | null)?.status;
const schemaMissing = computed(() => status(schemaQuery.error.value) === 404);
const recordMissing = computed(() => status(recordQuery.error.value) === 404
  || (!!recordQuery.data.value && recordQuery.data.value.moduleKey !== moduleKey.value));
const forbidden = computed(() => isForbidden(schemaQuery.error.value) || isForbidden(recordQuery.error.value));
const failed = computed(() => !forbidden.value && !schemaMissing.value && !recordMissing.value
  && (!!schemaQuery.error.value || !!recordQuery.error.value));

// A read-only workflow state locks the record: no delete, and edit only while the user can still move the status.
// A record an event has already posted is locked the same way (only the status may change: that is the cancel flow).
const workflow = computed(() => schemaQuery.data.value?.workflow);
const currentState = computed(() => (workflow.value ? recordQuery.data.value?.values[workflow.value.statusFieldKey] : undefined));
const frozen = computed(() => !!recordQuery.data.value?.frozenByEffects);
const stateLocked = computed(() => isReadOnlyState(workflow.value, currentState.value) || frozen.value);
const canEdit = computed(() => !!schemaQuery.data.value?.permissions.edit && (!stateLocked.value || canAdvance(workflow.value, currentState.value)));
const canDelete = computed(() => !!schemaQuery.data.value?.permissions.delete && !stateLocked.value);

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
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="schemaMissing" class="text-negative">{{ t('dynamic.moduleNotFound') }}</div>
    <div v-else-if="recordMissing" class="text-negative">{{ t('dynamic.recordNotFound') }}</div>
    <div v-else-if="failed" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <template v-else-if="schemaQuery.data.value && recordQuery.data.value">
      <div class="row items-center q-mb-md q-gutter-sm">
        <q-btn flat icon="arrow_back" :label="t('dynamic.back')" :to="`/dynamic/${moduleKey}`" />
        <div v-if="schemaQuery.data.value.titleFieldKey || schemaQuery.data.value.kind === 'extension'" class="text-h6">{{ recordQuery.data.value.title }}</div>
        <q-space />
        <PrintMenu :module-key="moduleKey" :record-id="recordId" />
        <q-btn v-if="canEdit" color="primary" :label="t('dynamic.edit')" :to="`/dynamic/${moduleKey}/${recordId}/edit`" />
        <q-btn v-if="canDelete" color="negative" outline :label="t('dynamic.delete')" @click="confirming = true" />
      </div>
      <div v-if="schemaQuery.data.value.readOnly" class="row items-center no-wrap q-mb-sm text-muted" data-testid="closed-banner">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('dynamic.closedBanner') }}</span>
      </div>
      <div v-if="frozen" class="row items-center no-wrap q-mb-sm text-muted" data-testid="frozen-notice">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('ledger.frozen') }}</span>
      </div>
      <RecordLedger v-if="schemaQuery.data.value.ledger" :record-id="recordId" :module-key="moduleKey" />
      <RecordConversions :record-id="recordId" :row-version="recordQuery.data.value.rowVersion" :closed="schemaQuery.data.value.readOnly" />
      <DynamicDetail :schema="schemaQuery.data.value" :record="recordQuery.data.value" />
      <DetailTables v-if="schemaQuery.data.value.details?.length" class="q-mt-md" :details="schemaQuery.data.value.details" :rows="recordQuery.data.value.details ?? {}" />
      <RecordTasks :record-id="recordId" :closed="!!schemaQuery.data.value.readOnly" />
      <RecordEffects :record-id="recordId" />
      <RelatedTabs :record-id="recordId" />
      <RecordHistory :record-id="recordId" />
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
