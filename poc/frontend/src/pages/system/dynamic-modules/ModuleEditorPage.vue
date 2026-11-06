<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useDesignerMutation, useModuleDetail } from 'src/application/dynamic/queries/modules-queries';
import { useEntities } from 'src/application/dynamic/queries/relation-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DeleteTargetRef, DesignerBlock, DesignerField, I18nText } from 'src/domain/dynamic/types';
import { errorFor, notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import DeleteDialog from 'src/presentation/dynamic/designer/DeleteDialog.vue';
import FieldEditorDialog from 'src/presentation/dynamic/designer/FieldEditorDialog.vue';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import IconSelect from 'src/presentation/dynamic/designer/IconSelect.vue';
import RulesTab from 'src/presentation/dynamic/rules/RulesTab.vue';
import PreviewTab from 'src/presentation/dynamic/designer/PreviewTab.vue';
import PermissionsTab from 'src/presentation/dynamic/permissions/PermissionsTab.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t, locale } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const qc = useQueryClient();
const moduleKey = computed(() => String(route.params.moduleKey));
const tab = ref<'fields' | 'rules' | 'permissions' | 'preview'>('fields');
const query = useModuleDetail(moduleKey);
const mod = computed(() => query.data.value);
const forbidden = computed(() => isForbidden(query.error.value));
const notFound = computed(() => (query.error.value as ApiError | null)?.status === 404);

const exporting = ref(false);
async function exportTemplate() {
  exporting.value = true;
  try {
    const { blob, fileName } = await dynamicApi.exportTemplate(moduleKey.value);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    // 立刻 revoke 可能讓瀏覽器還沒開始下載就失效，延後釋放
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (e) {
    fail(e);
  } finally {
    exporting.value = false;
  }
}

const fail = (e: unknown, shown: string[] = []) =>
  notifyApiError((o) => $q.notify(o), e as ApiError, shown, t('designer.saveFailed'));
const mutation = <V, R>(fn: (v: V) => Promise<R>) => useDesignerMutation(fn, () => moduleKey.value);

// ---- delete (module / block / field) ----
const deleteDialog = ref(false);
const deleteTarget = ref<DeleteTargetRef | null>(null);
const deleteName = ref('');
function askDelete(target: DeleteTargetRef, name: string) {
  deleteTarget.value = target;
  deleteName.value = name;
  deleteDialog.value = true;
}
async function onDeleted() {
  if (deleteTarget.value?.kind === 'module') {
    qc.removeQueries({ queryKey: ['module', moduleKey.value] });
    await Promise.all([qc.invalidateQueries({ queryKey: ['modules'] }), qc.invalidateQueries({ queryKey: ['menu'] })]);
    await router.push('/system/dynamic-modules');
    return;
  }
  await Promise.all(['module', 'schema', 'views', 'records', 'modules', 'rollup-sources'].map((k) => qc.invalidateQueries({ queryKey: [k] })));
}

// ---- blocks ----
const selectedBlockId = ref<number | null>(null);
watch(mod, (m) => {
  if (m && !m.blocks.some((b) => b.id === selectedBlockId.value)) selectedBlockId.value = m.blocks[0]?.id ?? null;
}, { immediate: true });
const block = computed(() => mod.value?.blocks.find((b) => b.id === selectedBlockId.value));

const blockDialog = ref(false);
const blockForm = ref<{ id?: number; label: I18nText; collapsed: boolean; columns: number }>({ label: {}, collapsed: false, columns: 2 });
const blockError = ref<ApiError | null>(null);
const blockInput = ref<{ validate(): Promise<boolean> } | null>(null);
const addBlock = mutation((label: I18nText) => dynamicApi.addBlock(moduleKey.value, label));
const updateBlock = mutation((v: { id: number; label: I18nText; collapsed: boolean; columns: number }) =>
  dynamicApi.updateBlock(v.id, { label: v.label, collapsed: v.collapsed, columns: v.columns }));

function openBlock(b?: DesignerBlock) {
  blockForm.value = b ? { id: b.id, label: { ...b.label }, collapsed: b.collapsed, columns: b.columns } : { label: {}, collapsed: false, columns: 2 };
  blockError.value = null;
  blockDialog.value = true;
}
async function submitBlock() {
  if (!(await blockInput.value?.validate())) return;
  const { id, label, collapsed, columns } = blockForm.value;
  const opts = {
    onSuccess: () => { blockDialog.value = false; },
    onError: (e: unknown) => { blockError.value = e as ApiError; fail(e, ['Label']); },
  };
  if (id === undefined) addBlock.mutate(label, opts);
  else updateBlock.mutate({ id, label, collapsed, columns }, opts);
}

// ---- fields ----
const typeLabel = (f: DesignerField) => t(`designer.types.${f.type}`);
const setStatus = mutation((v: { id: number; status: 'active' | 'inactive' }) => dynamicApi.setFieldStatus(v.id, v.status));
const reorder = mutation((v: { blockId: number; ids: number[] }) => dynamicApi.reorderFields(v.blockId, v.ids));
function move(index: number, delta: -1 | 1) {
  const b = block.value;
  if (!b) return;
  const ids = b.fields.map((f) => f.id);
  [ids[index], ids[index + delta]] = [ids[index + delta]!, ids[index]!];
  reorder.mutate({ blockId: b.id, ids }, { onError: (e) => fail(e) });
}

const fieldDialog = ref(false);
const editingFieldId = ref<number | null>(null);
const editingField = computed(() => block.value?.fields.find((f) => f.id === editingFieldId.value));
function openField(f?: DesignerField) {
  editingFieldId.value = f?.id ?? null;
  fieldDialog.value = true;
}
function onCreated(f: DesignerField) {
  if (f.type === 'enum' || f.type === 'multienum') {
    editingFieldId.value = f.id; // R24b: stay open in edit mode so options can be added
    $q.notify({ type: 'info', message: t('designer.optionsAfterCreate') });
  } else {
    fieldDialog.value = false;
  }
}

// ---- extension host ----
const isExtension = computed(() => mod.value?.kind === 'extension');
const entities = useEntities();
const hostName = computed(() => {
  const key = mod.value?.hostEntityKey;
  const e = entities.data.value?.find((x) => x.key === key);
  return e ? resolveI18n(e.name, locale.value) : (key ?? '');
});

// ---- title field ----
const titleOptions = computed(() =>
  (mod.value?.blocks ?? []).flatMap((b) => b.fields)
    .filter((f) => (f.type === 'text' || f.type === 'autonumber') && f.status === 'Active')
    .map((f) => ({ value: f.id, label: resolveI18n(f.label, locale.value) })));
const titleSel = useSelectFilter(titleOptions);
const titleError = ref<ApiError | null>(null);
// The menu shows the icon, so module updates also refresh it.
const updateModule = useDesignerMutation((patch: { titleFieldId?: number; icon?: string; section?: string }) => {
  const m = mod.value!;
  return dynamicApi.updateModule(m.key, { name: m.name, icon: m.icon, section: m.section, titleFieldId: m.titleFieldId, ...patch });
}, () => moduleKey.value, ['menu']);
function setTitle(id: number) {
  titleError.value = null;
  updateModule.mutate({ titleFieldId: id }, { onError: (e) => { titleError.value = e as unknown as ApiError; fail(e, ['TitleFieldId']); } });
}
function setIcon(icon: string) {
  updateModule.mutate({ icon }, { onError: (e) => fail(e) });
}
// Blank clears the section: `section: undefined` overrides the current value and is dropped from the JSON body.
const sectionDraft = ref('');
watch(() => mod.value?.section, (s) => { sectionDraft.value = s ?? ''; }, { immediate: true });
function saveSection() {
  const section = sectionDraft.value.trim() || undefined;
  sectionDraft.value = section ?? '';
  if (section === mod.value?.section) return;
  updateModule.mutate({ section }, { onError: (e) => fail(e, ['Section']) });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="notFound" class="text-negative">{{ t('designer.notFound') }}</div>
    <div v-else-if="query.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <div v-else-if="!mod" class="text-muted">{{ t('designer.loading') }}</div>
    <template v-else>
      <div class="row items-center q-gutter-md q-mb-md">
        <q-btn flat dense icon="arrow_back" to="/system/dynamic-modules" />
        <div class="text-h6">{{ resolveI18n(mod.name, locale) }} <span class="text-caption">({{ mod.key }})</span></div>
        <q-badge>{{ t(`designer.moduleStatus.${mod.status}`) }}</q-badge>
        <q-space />
        <q-btn outline dense icon="download" :label="t('designer.template.export')" :loading="exporting" data-testid="export-template" @click="exportTemplate" />
        <q-btn
          v-if="mod.status !== 'Active'" outline dense color="negative" icon="delete" :label="t('designer.delete.module')" data-testid="delete-module"
          @click="askDelete({ kind: 'module', key: mod.key }, resolveI18n(mod.name, locale))"
        />
        <IconSelect :model-value="mod.icon ?? ''" :label="t('designer.icon')" style="min-width: 220px" @update:model-value="setIcon" />
        <q-input v-model="sectionDraft" :label="t('designer.section')" :hint="t('designer.sectionHint')" maxlength="100" outlined dense
          style="min-width: 220px" data-testid="module-section" @change="saveSection" />
        <q-input
          v-if="isExtension" :model-value="hostName" :label="t('designer.extension.hostEntity')" readonly outlined dense
          style="min-width: 260px" data-testid="host-entity"
        />
        <q-select v-else :model-value="mod.titleFieldId ?? null" :options="titleSel.filtered.value" :label="t('designer.titleField')"
          :hint="t('designer.titleFieldHint')" emit-value map-options outlined dense style="min-width: 260px"
          data-testid="title-field" :error="!!errorFor(titleError, 'TitleFieldId') || undefined"
          :error-message="errorFor(titleError, 'TitleFieldId')" @update:model-value="setTitle" use-input input-debounce="0" fill-input hide-selected @filter="titleSel.onFilter" @popup-hide="titleSel.reset">
          <template #no-option><SelectNoOption /></template>
        </q-select>
      </div>

      <q-tabs v-model="tab" dense align="left" class="q-mb-md" data-testid="designer-tabs">
        <q-tab name="fields" :label="t('designer.fieldsTab')" />
        <q-tab name="rules" :label="t('designer.rules.tab')" data-testid="tab-rules" />
        <q-tab name="permissions" :label="t('designer.permissions.tab')" data-testid="tab-permissions" />
        <q-tab name="preview" :label="t('designer.preview.tab')" data-testid="tab-preview" />
      </q-tabs>
      <q-tab-panels v-model="tab" animated keep-alive>
        <q-tab-panel name="fields" class="q-pa-none">
        <div class="row q-col-gutter-md">
          <div class="col-12 col-md-4">
            <div class="row items-center q-mb-sm">
              <div class="text-subtitle1">{{ t('designer.blocks') }}</div>
              <q-space />
              <q-btn dense color="primary" icon="add" :label="t('designer.addBlock')" data-testid="add-block" @click="openBlock()" />
            </div>
            <q-list bordered separator>
              <q-item v-if="!mod.blocks.length"><q-item-section class="text-muted">{{ t('designer.noBlocks') }}</q-item-section></q-item>
              <q-item v-for="b in mod.blocks" :key="b.id" clickable :active="b.id === selectedBlockId" @click="selectedBlockId = b.id">
                <q-item-section>{{ resolveI18n(b.label, locale) }}</q-item-section>
                <q-item-section side>
                  <div class="row no-wrap items-center">
                    <q-btn flat dense round icon="edit" :title="t('designer.editBlock')" @click.stop="openBlock(b)" />
                    <q-btn
                      flat dense round color="negative" icon="delete" :title="t('designer.delete.action')" :data-testid="`delete-block-${b.id}`"
                      @click.stop="askDelete({ kind: 'block', id: b.id }, resolveI18n(b.label, locale))"
                    />
                  </div>
                </q-item-section>
              </q-item>
            </q-list>
          </div>

          <div class="col-12 col-md-8">
            <div class="row items-center q-mb-sm">
              <div class="text-subtitle1">{{ t('designer.fields') }}</div>
              <q-space />
              <q-btn dense color="primary" icon="add" :label="t('designer.addField')" :disable="!block" data-testid="add-field" @click="openField()" />
            </div>
            <div v-if="!block" class="text-muted">{{ t('designer.selectBlock') }}</div>
            <q-list v-else bordered separator>
              <q-item v-if="!block.fields.length"><q-item-section class="text-muted">{{ t('designer.noFields') }}</q-item-section></q-item>
              <q-item v-for="(f, i) in block.fields" :key="f.id" :class="{ 'text-muted': f.status === 'Inactive' }" :data-testid="`field-${f.id}`">
                <q-item-section>
                  <q-item-label>
                    {{ resolveI18n(f.label, locale) }}
                    <q-badge v-if="f.status === 'Inactive'" color="muted" class="q-ml-sm">{{ t('designer.fieldInactive') }}</q-badge>
                  </q-item-label>
                  <q-item-label caption>{{ typeLabel(f) }} · {{ f.key }}</q-item-label>
                </q-item-section>
                <q-item-section side>
                  <div class="row no-wrap items-center">
                    <q-btn flat dense round icon="arrow_upward" :title="t('designer.moveUp')" :disable="i === 0" @click="move(i, -1)" />
                    <q-btn flat dense round icon="arrow_downward" :title="t('designer.moveDown')" :disable="i === block.fields.length - 1" @click="move(i, 1)" />
                    <q-btn flat dense round icon="edit" :title="t('designer.edit')" @click="openField(f)" />
                    <q-btn
                      v-if="f.status === 'Active'" flat dense color="negative" :label="t('designer.deactivate')"
                      @click="setStatus.mutate({ id: f.id, status: 'inactive' }, { onError: (e) => fail(e) })"
                    />
                    <q-btn
                      v-else flat dense color="primary" :label="t('designer.reactivate')"
                      @click="setStatus.mutate({ id: f.id, status: 'active' }, { onError: (e) => fail(e) })"
                    />
                    <q-btn
                      v-if="f.status === 'Inactive'" flat dense round color="negative" icon="delete" :title="t('designer.delete.action')"
                      :data-testid="`delete-field-${f.id}`" @click="askDelete({ kind: 'field', id: f.id }, resolveI18n(f.label, locale))"
                    />
                  </div>
                </q-item-section>
              </q-item>
            </q-list>
          </div>
        </div>
        </q-tab-panel>
        <q-tab-panel name="rules" class="q-pa-none">
          <RulesTab :module="mod" />
        </q-tab-panel>
        <q-tab-panel name="permissions" class="q-pa-none">
          <PermissionsTab :module-key="moduleKey" :fields="mod?.blocks.flatMap((b) => b.fields) ?? []" :title-field-id="mod?.titleFieldId" :detail-of="mod?.detailOf" />
        </q-tab-panel>
        <q-tab-panel name="preview" class="q-pa-none">
          <PreviewTab :module-key="moduleKey" :version="mod.version" />
        </q-tab-panel>
      </q-tab-panels>

      <q-dialog v-model="blockDialog" persistent>
        <q-card style="min-width: 420px; max-width: 90vw">
          <q-card-section class="text-h6">{{ blockForm.id === undefined ? t('designer.addBlock') : t('designer.editBlock') }}</q-card-section>
          <q-card-section class="q-gutter-md">
            <I18nTextInput ref="blockInput" v-model="blockForm.label" :label="t('designer.blockLabel')" :error="errorFor(blockError, 'Label')" />
            <q-toggle v-if="blockForm.id !== undefined" v-model="blockForm.collapsed" :label="t('designer.collapsed')" />
            <div v-if="blockForm.id !== undefined">
              <div class="text-caption text-muted">{{ t('designer.columns') }}</div>
              <q-btn-toggle
                v-model="blockForm.columns" data-testid="block-columns" no-caps unelevated toggle-color="primary"
                :options="[1, 2, 3].map((n) => ({ label: String(n), value: n }))"
              />
            </div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('designer.close')" @click="blockDialog = false" />
            <q-btn color="primary" :label="t('designer.save')" @click="submitBlock" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <DeleteDialog v-model="deleteDialog" :target="deleteTarget" :name="deleteName" @deleted="onDeleted" />

      <FieldEditorDialog
        v-if="block" v-model="fieldDialog" :module-key="moduleKey" :block-id="block.id" :field="editingField" @created="onCreated"
      />
    </template>
  </q-page>
</template>
