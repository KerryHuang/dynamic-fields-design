<script setup lang="ts">
import { Notify, useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useDesignerMutation, useModules } from 'src/application/dynamic/queries/modules-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useEntities } from 'src/application/dynamic/queries/relation-queries';
import { useImportTemplate } from 'src/application/dynamic/queries/templates-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { I18nText } from 'src/domain/dynamic/types';
import { errorFor, notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import TemplateDialog from 'src/presentation/dynamic/designer/TemplateDialog.vue';
import IconSelect, { MODULE_ICONS } from 'src/presentation/dynamic/designer/IconSelect.vue';
import { dynamicApi, type ModuleSummary } from 'src/services/api/dynamic-api';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { isForbidden, type ApiError } from 'src/services/api/http';

const KEY_RE = /^[a-z][a-z0-9-]{1,39}$/;
const { t, locale } = useI18n();
const $q = useQuasar();
const router = useRouter();
const modules = useModules();
const forbidden = computed(() => isForbidden(modules.error.value));

const columns = computed<QTableColumn<ModuleSummary>[]>(() => [
  { name: 'key', label: t('designer.key'), align: 'left', field: 'key' },
  { name: 'name', label: t('designer.name'), align: 'left', field: (m) => resolveI18n(m.name, locale.value) },
  { name: 'programCode', label: t('designer.programCode'), align: 'left', field: 'programCode' },
  { name: 'status', label: t('designer.status'), align: 'left', field: (m) => t(`designer.moduleStatus.${m.status}`) },
  { name: 'version', label: t('designer.version'), align: 'right', field: 'version' },
  { name: 'actions', label: '', align: 'right', field: 'key' },
]);

const headerName = (key: string) => {
  const h = modules.data.value?.find((m) => m.key === key);
  return h ? resolveI18n(h.name, locale.value) : key;
};

const notifyFail = (e: unknown, shown: string[] = []) =>
  notifyApiError((o) => $q.notify(o), e as ApiError, shown, t('designer.saveFailed'));
const activate = useDesignerMutation((key: string) => dynamicApi.activateModule(key), (key) => key, ['menu']);
const deactivate = useDesignerMutation((key: string) => dynamicApi.deactivateModule(key), (key) => key, ['menu']);
const toggle = (m: ModuleSummary, on: boolean) =>
  (on ? activate : deactivate).mutate(m.key, {
    onSuccess: () => $q.notify({ type: 'positive', message: t(on ? 'designer.activated' : 'designer.deactivated') }),
    onError: (e) => notifyFail(e),
  });

const me = useMe();
const canAdd = computed(() => !!me.data.value?.designer.add);
// 從範本建立會重播修改指令（區塊、欄位、規則），只有新增權限一定 403
const canInstantiate = computed(() => canAdd.value && !!me.data.value?.designer.edit);
const templateDialog = ref(false);
const fileInput = ref<HTMLInputElement | null>(null);
const importErrors = ref<string[]>([]);
const importTemplate = useImportTemplate();
async function onFile(ev: Event) {
  const input = ev.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  const text = await file.text();
  input.value = '';
  importTemplate.mutate(text, {
    onSuccess: (tpl) => Notify.create({ type: 'positive', message: t('designer.template.importDone', { name: resolveI18n(tpl.name, locale.value) }) }),
    onError: (e) => {
      const err = e as unknown as ApiError;
      if (err.errors?.template?.length) importErrors.value = err.errors.template;
      else notifyFail(e);
    },
  });
}

const dialog = ref(false);
const emptyForm = () => ({ key: '', name: {} as I18nText, icon: MODULE_ICONS[0]!, section: '', kind: 'standalone' as 'standalone' | 'extension', hostEntityKey: null as string | null });
const form = ref(emptyForm());
const entities = useEntities();
const kindOptions = computed(() => (['standalone', 'extension'] as const).map((v) => ({ value: v, label: t(`designer.extension.${v}`) })));
const entityOptions = computed(() => (entities.data.value ?? []).map((e) => ({ value: e.key, label: resolveI18n(e.name, locale.value) })));
const hostSel = useSelectFilter(entityOptions);
const hostError = ref(false);
const error = ref<ApiError | null>(null);
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
const create = useDesignerMutation(
  () => dynamicApi.createModule({
    key: form.value.key, name: form.value.name, icon: form.value.icon, section: form.value.section.trim() || undefined, kind: form.value.kind,
    ...(form.value.kind === 'extension' ? { hostEntityKey: form.value.hostEntityKey! } : {}),
  }),
  () => form.value.key,
);

function openDialog() {
  form.value = emptyForm();
  error.value = null;
  hostError.value = false;
  dialog.value = true;
}
async function submit() {
  if (!(await nameInput.value?.validate())) return;
  error.value = null;
  hostError.value = form.value.kind === 'extension' && !form.value.hostEntityKey;
  if (hostError.value) return;
  create.mutate(undefined, {
    onSuccess: () => { dialog.value = false; router.push(`/system/dynamic-modules/${form.value.key}`); },
    onError: (e) => { error.value = e as unknown as ApiError; notifyFail(e, ['Key', 'Name', 'HostEntityKey', 'Section']); },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else class="row items-center q-mb-md">
      <div class="text-h6">{{ t('designer.modules') }}</div>
      <q-space />
      <template v-if="canAdd">
        <q-btn outline color="primary" icon="upload_file" class="q-mr-sm" :label="t('designer.template.import')" data-testid="import-template" @click="fileInput?.click()" />
        <q-btn v-if="canInstantiate" outline color="primary" icon="library_add" class="q-mr-sm" :label="t('designer.template.fromTemplate')" data-testid="from-template" @click="templateDialog = true" />
        <input ref="fileInput" type="file" accept=".json,application/json" hidden data-testid="import-template-input" @change="onFile">
      </template>
      <q-btn color="primary" icon="add" :label="t('designer.newModule')" data-testid="new-module" @click="openDialog" />
    </div>
    <q-table
      v-if="!forbidden" flat bordered row-key="id" :rows="modules.data.value ?? []" :columns="columns" :loading="modules.isFetching.value"
      :rows-per-page-options="[0]" hide-pagination @row-click="(_e, row) => router.push(`/system/dynamic-modules/${row.key}`)"
    >
      <template #body-cell-name="{ row, value }">
        <q-td>
          {{ value }}
          <q-badge v-if="row.detailOf" outline color="primary" class="q-ml-sm" :data-testid="`detail-of-${row.key}`">
            {{ t('designer.detailOf', { header: headerName(row.detailOf) }) }}
          </q-badge>
        </q-td>
      </template>
      <template #body-cell-actions="{ row }">
        <q-td class="text-right" @click.stop>
          <q-btn flat dense color="primary" :label="t('designer.activate')" :disable="row.status === 'Active'" @click="toggle(row, true)" />
          <q-btn flat dense color="negative" :label="t('designer.deactivate')" :disable="row.status !== 'Active'" @click="toggle(row, false)" />
        </q-td>
      </template>
    </q-table>

    <TemplateDialog v-if="templateDialog" v-model="templateDialog" />
    <q-dialog :model-value="importErrors.length > 0" @update:model-value="importErrors = []">
      <q-card style="min-width: 400px" data-testid="import-errors">
        <q-card-section class="text-h6">{{ t('designer.template.import') }}</q-card-section>
        <q-card-section class="text-negative">
          <div v-for="m in importErrors" :key="m">{{ m }}</div>
        </q-card-section>
        <q-card-actions align="right"><q-btn v-close-popup flat :label="t('designer.close')" /></q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog v-model="dialog" persistent>
      <q-card style="min-width: 480px; max-width: 90vw">
        <q-card-section class="text-h6">{{ t('designer.newModule') }}</q-card-section>
        <q-card-section class="q-gutter-md">
          <q-input
            v-model="form.key" :label="`${t('designer.key')} *`" :hint="t('designer.keyHint')" outlined dense
            :rules="[(v: string) => KEY_RE.test(v) || t('designer.keyInvalid')]" lazy-rules
            :error="!!errorFor(error, 'Key') || undefined" :error-message="errorFor(error, 'Key')"
          />
          <I18nTextInput ref="nameInput" v-model="form.name" :label="t('designer.name')" :error="errorFor(error, 'Name')" />
          <IconSelect v-model="form.icon" :label="t('designer.icon')" />
          <q-input v-model="form.section" :label="t('designer.section')" :hint="t('designer.sectionHint')" maxlength="100" outlined dense
            data-testid="module-section" :error="!!errorFor(error, 'Section') || undefined" :error-message="errorFor(error, 'Section')" />
          <div data-testid="module-kind">
            <div class="text-caption text-muted">{{ t('designer.extension.kind') }}</div>
            <q-option-group v-model="form.kind" :options="kindOptions" inline dense />
          </div>
          <div v-if="form.kind === 'extension'" data-testid="host-entity">
            <q-select v-model="form.hostEntityKey" :options="hostSel.filtered.value" :label="`${t('designer.extension.hostEntity')} *`"
              emit-value map-options outlined dense
              :error="hostError || !!errorFor(error, 'HostEntityKey')"
              :error-message="hostError ? t('designer.extension.hostEntityRequired') : errorFor(error, 'HostEntityKey')" use-input input-debounce="0" fill-input hide-selected @filter="hostSel.onFilter" @popup-hide="hostSel.reset">
              <template #no-option><SelectNoOption /></template>
            </q-select>
          </div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('designer.close')" @click="dialog = false" />
          <q-btn color="primary" :label="t('designer.create')" :disable="!KEY_RE.test(form.key)" :loading="create.isPending.value" @click="submit" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-page>
</template>
