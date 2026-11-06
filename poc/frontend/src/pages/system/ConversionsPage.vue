<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useConversions, useDeleteConversionMutation } from 'src/application/dynamic/queries/conversions-queries';
import { useModules } from 'src/application/dynamic/queries/modules-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { Conversion } from 'src/domain/dynamic/types';
import ConversionEditorDialog from 'src/presentation/dynamic/conversions/ConversionEditorDialog.vue';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t, locale } = useI18n();
const $q = useQuasar();
const conversions = useConversions();
const modules = useModules();
const me = useMe();
const forbidden = computed(() => isForbidden(conversions.error.value));
const canEdit = computed(() => !!me.data.value?.designer.edit);

const moduleName = (key: string) => {
  const m = modules.data.value?.find((x) => x.key === key);
  return m ? resolveI18n(m.name, locale.value) : key;
};
const columns = computed<QTableColumn<Conversion>[]>(() => [
  { name: 'name', label: t('docflow.conversion.name'), align: 'left', field: (c) => resolveI18n(c.name, locale.value) },
  { name: 'key', label: t('docflow.conversion.key'), align: 'left', field: 'key' },
  { name: 'flow', label: t('docflow.conversion.flow'), align: 'left', field: (c) => `${moduleName(c.sourceModuleKey)} → ${moduleName(c.targetModuleKey)}` },
  { name: 'active', label: t('docflow.conversion.active'), align: 'left', field: (c) => (c.active ? t('docflow.conversion.on') : t('docflow.conversion.off')) },
  { name: 'actions', label: '', align: 'right', field: 'key' },
]);

const dialog = ref(false);
const editing = ref<Conversion | undefined>();
function open(c?: Conversion) {
  editing.value = c;
  dialog.value = true;
}

const deleting = ref<Conversion | null>(null);
const remove = useDeleteConversionMutation();
function confirmDelete() {
  remove.mutate(deleting.value!.key, {
    onSuccess: () => { deleting.value = null; $q.notify({ type: 'positive', message: t('docflow.conversion.deleted') }); },
    onError: (e) => $q.notify({ type: 'negative', message: leftoverMessages(e as unknown as ApiError, []).join('、') || t('designer.saveFailed') }),
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('docflow.conversion.title') }}</div>
        <q-space />
        <q-btn v-if="canEdit" color="primary" icon="add" :label="t('docflow.conversion.create')" data-testid="conversion-create" @click="open()" />
      </div>
      <div class="text-caption text-muted q-mb-md">{{ t('docflow.conversion.pageHint') }}</div>
      <div v-if="conversions.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
      <q-table
        v-else flat bordered row-key="key" :rows="conversions.data.value ?? []" :columns="columns" :loading="conversions.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination :no-data-label="t('docflow.conversion.empty')"
      >
        <template #body="{ row, cols }">
          <q-tr :data-testid="`conversion-row-${row.key}`">
            <q-td v-for="c in cols" :key="c.name" :align="c.align">
              <template v-if="c.name === 'actions'">
                <q-btn flat dense color="primary" :label="t('designer.edit')" :disable="!canEdit" :data-testid="`conversion-edit-${row.key}`" @click="open(row)" />
                <q-btn flat dense color="negative" :label="t('dynamic.delete')" :disable="!canEdit" :data-testid="`conversion-delete-${row.key}`" @click="deleting = row" />
              </template>
              <template v-else>{{ c.value }}</template>
            </q-td>
          </q-tr>
        </template>
      </q-table>

      <ConversionEditorDialog v-model="dialog" :conversion="editing" />

      <q-dialog :model-value="deleting !== null" @update:model-value="deleting = null">
        <q-card>
          <q-card-section>{{ t('docflow.conversion.deleteConfirm', { name: deleting ? resolveI18n(deleting.name, locale) : '' }) }}</q-card-section>
          <q-card-actions align="right">
            <q-btn v-close-popup flat :label="t('dynamic.cancel')" />
            <q-btn color="negative" :label="t('dynamic.delete')" :loading="remove.isPending.value" data-testid="conversion-delete-confirm" @click="confirmDelete" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
