<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useRecord, useSaveRecord } from 'src/application/dynamic/queries/records-queries';
import { useModuleSchema } from 'src/application/dynamic/queries/schema-queries';
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import DynamicForm from 'src/presentation/dynamic/components/DynamicForm.vue';
import { messagesOutside, SERVER_STALE_MESSAGE } from 'src/presentation/dynamic/designer/api-error';
import { parseDetailKey } from 'src/presentation/dynamic/details/detail-rows';
import type { DetailRowPayload, ReferenceInfo } from 'src/domain/dynamic/types';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const moduleKey = computed(() => String(route.params.moduleKey));
const recordId = computed(() => Number(route.params.id));
const isEdit = computed(() => route.params.id !== undefined);

const schemaQuery = useModuleSchema(moduleKey);
const recordQuery = useRecord(recordId);
const save = useSaveRecord();
const serverErrors = ref<Record<string, string[]>>();
const detailErrors = ref<Record<string, string[]>>();
const form = ref<InstanceType<typeof DynamicForm>>();
// Set by an attachment upload (it bumps the stored rowVersion); a reloaded record supersedes it.
const uploadedRowVersion = ref<string>();
watch(() => recordQuery.data.value?.rowVersion, () => { uploadedRowVersion.value = undefined; });

const isExtensionNew = computed(() => !isEdit.value && schemaQuery.data.value?.kind === 'extension');
const hostKey = ref<string | null>(null);
const hostError = ref<string>();
const hostLightUrl = computed(() => `/entities/${schemaQuery.data.value?.hostEntityKey}/light`);

// ?prefill={fieldKey}:{value}: only reference fields of this module; module targets take a number.
const prefill = computed<Record<string, unknown>>(() => {
  const raw = route.query.prefill;
  const text = Array.isArray(raw) ? raw[0] : raw;
  const i = text?.indexOf(':') ?? -1;
  if (!text || i < 0) return {};
  const field = schemaQuery.data.value?.blocks.flatMap((b) => b.fields).find((f) => f.key === text.slice(0, i) && f.type === 'reference');
  const value = text.slice(i + 1);
  if (!field || !value) return {};
  if (field.reference?.kind !== 'module') return { [field.key]: value };
  const n = Number(value);
  return Number.isNaN(n) ? {} : { [field.key]: n };
});

// A reverse-tab prefill carries only the target id; fetch the target so the field shows its title, not the id.
const prefillTarget = computed(() => {
  const [key, value] = Object.entries(prefill.value)[0] ?? [];
  const field = schemaQuery.data.value?.blocks.flatMap((b) => b.fields).find((f) => f.key === key);
  return field?.reference?.kind === 'module' ? { key: key!, id: Number(value) } : undefined;
});
const targetQuery = useRecord(computed(() => prefillTarget.value?.id ?? Number.NaN));
const prefillReferences = computed<Record<string, ReferenceInfo> | undefined>(() => {
  const target = prefillTarget.value;
  if (!target) return undefined;
  const key = String(target.id);
  if (targetQuery.data.value) return { [target.key]: { key, title: targetQuery.data.value.title, state: 'ok' } };
  if (targetQuery.error.value) return { [target.key]: { key, state: status(targetQuery.error.value) === 404 ? 'missing' : 'restricted' } };
  return undefined;
});

const status = (e: unknown) => (e as ApiError | null)?.status;
const schemaMissing = computed(() => status(schemaQuery.error.value) === 404);
const recordMissing = computed(() => isEdit.value && (status(recordQuery.error.value) === 404
  || (!!recordQuery.data.value && recordQuery.data.value.moduleKey !== moduleKey.value)));
const lacksPermission = computed(() => {
  const p = schemaQuery.data.value?.permissions;
  return !!p && !(isEdit.value ? p.edit : p.add);
});
const forbidden = computed(() => isForbidden(schemaQuery.error.value) || (isEdit.value && isForbidden(recordQuery.error.value)) || lacksPermission.value);
const failed = computed(() => !forbidden.value && !schemaMissing.value && !recordMissing.value
  && (!!schemaQuery.error.value || (isEdit.value && !!recordQuery.error.value)));
const ready = computed(() => schemaQuery.data.value && (!isEdit.value || recordQuery.data.value));
const back = () => router.push(isEdit.value ? `/dynamic/${moduleKey.value}/${recordId.value}` : `/dynamic/${moduleKey.value}`);

// A `details.` key counts as shown when it points at a submitted row and a field of that detail (or the row itself).
function shownOnRow(key: string): boolean {
  const p = parseDetailKey(key);
  const detail = p && schemaQuery.data.value?.details?.find((d) => d.moduleKey === p.childKey);
  return !!detail && p!.index < (form.value?.uidAt[p!.childKey]?.length ?? 0)
    && (p!.fieldKey === '_record' || detail.fields.some((f) => f.key === p!.fieldKey));
}

async function onSubmit(values: Record<string, unknown>, details: Record<string, DetailRowPayload[]>) {
  serverErrors.value = undefined;
  detailErrors.value = undefined;
  const sentDetails = Object.keys(details).length ? details : undefined;
  if (isEdit.value && Object.keys(values).length === 0 && !sentDetails) return back();
  if (isExtensionNew.value && !hostKey.value) {
    hostError.value = t('designer.extension.hostEntityRequired');
    return;
  }
  try {
    const rec = isEdit.value
      ? await save.mutateAsync({ moduleKey: moduleKey.value, id: recordId.value, values, rowVersion: uploadedRowVersion.value ?? recordQuery.data.value!.rowVersion, details: sentDetails })
      : await save.mutateAsync({ moduleKey: moduleKey.value, values, ...(isExtensionNew.value && { hostKey: hostKey.value! }), details: sentDetails });
    await router.push(`/dynamic/${moduleKey.value}/${rec.id}`);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 400 && err.errors) {
      const { hostKey: hostMessages, ...fieldErrors } = err.errors;
      if (hostMessages) hostError.value = hostMessages.join('、');
      const entries = Object.entries(fieldErrors);
      serverErrors.value = Object.fromEntries(entries.filter(([k]) => !k.startsWith('details.')));
      detailErrors.value = Object.fromEntries(entries.filter(([k]) => k.startsWith('details.')));
      // Keys that no rendered field shows (stale schema, model-binding errors): surface them and reload the schema.
      const keys = ['_record', ...(schemaQuery.data.value?.blocks.flatMap((b) => b.fields.map((f) => f.key)) ?? []), // _record is shown at the top of the form
        ...entries.map(([k]) => k).filter(shownOnRow)];
      const rest = messagesOutside(fieldErrors, keys);
      if (rest.length) {
        $q.notify({ type: 'negative', message: rest.join('、') });
        void schemaQuery.refetch();
      }
    }
    else if (err.status === 409 && err.message && err.message !== SERVER_STALE_MESSAGE) {
      if (isExtensionNew.value) hostError.value = err.message;
      else $q.notify({ type: 'warning', message: err.message });
    } else if (err.status === 409 && isEdit.value) {
      $q.notify({
        type: 'warning', message: t('dynamic.stale'),
        actions: [{ label: t('dynamic.reload'), color: 'white', handler: () => { serverErrors.value = undefined; detailErrors.value = undefined; void recordQuery.refetch(); } }],
      });
    } else $q.notify({ type: 'negative', message: err.message ?? t('designer.saveFailed') });
  }
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="schemaMissing" class="text-negative">{{ t('dynamic.moduleNotFound') }}</div>
    <div v-else-if="recordMissing" class="text-negative">{{ t('dynamic.recordNotFound') }}</div>
    <div v-else-if="failed" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <template v-else-if="ready">
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ isEdit ? t('dynamic.editTitle') : t('dynamic.newTitle') }}</div>
        <q-space />
        <q-btn flat :label="t('dynamic.cancel')" @click="back" />
      </div>
      <PopupSelect
        v-if="isExtensionNew" v-model="hostKey" :light-url="hostLightUrl" value-kind="string" :label="t('designer.extension.hostEntity')"
        :error="hostError" required class="q-mb-md" @update:model-value="hostError = undefined"
      />
      <DynamicForm
        ref="form" :schema="schemaQuery.data.value!" :mode="isEdit ? 'edit' : 'create'" :record="isEdit ? recordQuery.data.value : undefined" :initial="prefill" :references="prefillReferences"
        :server-errors="serverErrors" :detail-errors="detailErrors" :saving="save.isPending.value" @submit="onSubmit" @row-version="(v) => (uploadedRowVersion = v)"
      />
    </template>
  </q-page>
</template>
