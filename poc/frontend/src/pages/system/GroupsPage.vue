<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useAdminGroups, useAdminUsers, useCreateGroup, useDeleteGroup, useGroupImpact, useRenameGroup, useSetGroupMembers,
} from 'src/application/dynamic/queries/admin-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { AdminGroup, BlockingOption } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t, locale } = useI18n();
const $q = useQuasar();
const groups = useAdminGroups();
const users = useAdminUsers();
const forbidden = computed(() => isForbidden(groups.error.value));
const nameOf = (g: AdminGroup) => resolveI18n(g.nameI18n, locale.value);
const errorText = (e: unknown, fallback = t('groups.saveFailed')) => leftoverMessages(e as ApiError, []).join('、') || fallback;

const columns = computed<QTableColumn<AdminGroup>[]>(() => [
  { name: 'name', label: t('groups.name'), align: 'left', field: nameOf },
  { name: 'count', label: t('groups.memberCount'), align: 'left', field: (g) => g.memberUserIds.length },
  { name: 'actions', label: '', align: 'right', field: 'id' },
]);

// 新增／改名共用一個對話框，只編輯目前語系的名稱
const nameDialog = ref(false);
const editingId = ref<number | null>(null);
const name = ref('');
const nameError = ref('');
const create = useCreateGroup();
const rename = useRenameGroup();
const saving = computed(() => create.isPending.value || rename.isPending.value);
function openName(g?: AdminGroup) {
  editingId.value = g?.id ?? null;
  name.value = g ? nameOf(g) : '';
  nameError.value = '';
  nameDialog.value = true;
}
function saveName() {
  nameError.value = '';
  const handlers = {
    onSuccess: () => { nameDialog.value = false; $q.notify({ type: 'positive', message: t('groups.saved') }); },
    onError: (e: unknown) => { nameError.value = errorText(e); },
  };
  if (editingId.value !== null) rename.mutate({ id: editingId.value, name: name.value, locale: locale.value }, handlers);
  else create.mutate({ name: name.value, locale: locale.value }, handlers);
}

// 成員：只列啟用中的使用者；已在群組內但已停用的人保留勾選，儲存時不會被默默移除
const membersTarget = ref<AdminGroup | null>(null);
const selected = ref<string[]>([]);
const membersError = ref('');
const setMembers = useSetGroupMembers();
const memberOptions = computed(() => {
  const inGroup = new Set(membersTarget.value?.memberUserIds ?? []);
  return (users.data.value ?? []).filter((u) => u.isActive || inGroup.has(u.userId));
});
function openMembers(g: AdminGroup) {
  membersTarget.value = g;
  selected.value = [...g.memberUserIds];
  membersError.value = '';
}
function saveMembers() {
  membersError.value = '';
  setMembers.mutate({ id: membersTarget.value!.id, userIds: selected.value }, {
    onSuccess: () => { membersTarget.value = null; $q.notify({ type: 'positive', message: t('groups.saved') }); },
    onError: (e) => { membersError.value = errorText(e); },
  });
}

// 刪除：先查影響；有選項範圍指定時停用刪除，409（別人剛加了範圍）把清單換成後端回的
const deleteTarget = ref<AdminGroup | null>(null);
const conflictOptions = ref<BlockingOption[] | null>(null);
const deleteError = ref('');
const impact = useGroupImpact(computed(() => deleteTarget.value?.id ?? null));
const remove = useDeleteGroup();
const blocking = computed(() => conflictOptions.value ?? impact.data.value?.blockingOptions ?? []);
function openDelete(g: AdminGroup) {
  deleteTarget.value = g;
  conflictOptions.value = null;
  deleteError.value = '';
}
function confirmDelete() {
  remove.mutate(deleteTarget.value!.id, {
    onSuccess: () => { deleteTarget.value = null; $q.notify({ type: 'positive', message: t('groups.saved') }); },
    onError: (e) => {
      const err = e as unknown as ApiError;
      if (err.blockingOptions) conflictOptions.value = err.blockingOptions;
      else deleteError.value = errorText(e);
    },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('groups.title') }}</div>
        <q-space />
        <q-btn color="primary" icon="add" :label="t('groups.create')" data-testid="group-create" @click="openName()" />
      </div>
      <q-table
        flat bordered row-key="id" :rows="groups.data.value ?? []" :columns="columns" :loading="groups.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination
      >
        <template #body="{ row, cols }">
          <q-tr :data-testid="`group-row-${row.id}`">
            <q-td v-for="c in cols" :key="c.name" :align="c.align">
              <template v-if="c.name === 'actions'">
                <q-btn flat dense color="primary" :label="t('groups.rename')" :data-testid="`group-rename-${row.id}`" @click="openName(row)" />
                <q-btn flat dense color="primary" :label="t('groups.editMembers')" :data-testid="`group-members-${row.id}`" @click="openMembers(row)" />
                <q-btn flat dense color="negative" :label="t('groups.delete')" :data-testid="`group-delete-${row.id}`" @click="openDelete(row)" />
              </template>
              <template v-else>{{ c.value }}</template>
            </q-td>
          </q-tr>
        </template>
      </q-table>

      <q-dialog v-model="nameDialog">
        <q-card style="width: 480px; max-width: 95vw">
          <q-card-section class="text-h6">{{ editingId !== null ? t('groups.rename') : t('groups.create') }}</q-card-section>
          <q-card-section class="row q-col-gutter-md">
            <div class="col-12">
              <q-input v-model="name" :label="t('groups.name')" dense outlined data-testid="group-name" @keyup.enter="saveName" />
            </div>
            <div v-if="nameError" class="col-12 text-negative" data-testid="group-error">{{ nameError }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('groups.cancel')" data-testid="group-cancel" @click="nameDialog = false" />
            <q-btn color="primary" :label="t('groups.save')" :loading="saving" data-testid="group-save" @click="saveName" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="membersTarget !== null" @update:model-value="membersTarget = null">
        <q-card style="width: 640px; max-width: 95vw">
          <q-card-section class="text-h6">{{ t('groups.editMembers') }}：{{ membersTarget ? nameOf(membersTarget) : '' }}</q-card-section>
          <q-card-section class="row q-col-gutter-sm" style="max-height: 60vh; overflow: auto">
            <div v-for="u in memberOptions" :key="u.userId" class="col-12 col-sm-6">
              <q-checkbox
                v-model="selected" :val="u.userId" dense :data-testid="`member-${u.userId}`"
                :label="u.isActive ? `${u.displayName} (${u.userId})` : `${u.displayName} (${u.userId}) ${t('groups.inactive')}`"
              />
            </div>
            <div v-if="membersError" class="col-12 text-negative" data-testid="members-error">{{ membersError }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('groups.cancel')" data-testid="members-cancel" @click="membersTarget = null" />
            <q-btn color="primary" :label="t('groups.save')" :loading="setMembers.isPending.value" data-testid="members-save" @click="saveMembers" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="deleteTarget !== null" @update:model-value="deleteTarget = null">
        <q-card style="width: 520px; max-width: 95vw">
          <q-card-section class="text-h6">{{ t('groups.deleteTitle', { name: deleteTarget ? nameOf(deleteTarget) : '' }) }}</q-card-section>
          <q-card-section v-if="impact.data.value" class="column q-gutter-y-sm">
            <div data-testid="impact-members">{{ t('groups.impactMembers', { n: impact.data.value.memberCount }) }}</div>
            <div data-testid="impact-modules">
              {{ t('groups.impactModules', { list: impact.data.value.modules.join('、') || t('groups.impactNone') }) }}
            </div>
            <div data-testid="impact-fields">{{ t('groups.impactFields', { n: impact.data.value.fieldPermissionCount }) }}</div>
            <div data-testid="impact-scopes">{{ t('groups.impactScopes', { n: impact.data.value.dataScopeCount }) }}</div>
            <div data-testid="impact-reports">
              {{ t('groups.impactReports', { list: impact.data.value.reports.join('、') || t('groups.impactNone') }) }}
            </div>
            <div class="text-warning" data-testid="impact-warning">{{ t('groups.loseWarning') }}</div>
            <div v-if="blocking.length" class="text-negative" data-testid="impact-blocking">
              <div>{{ t('groups.blocking') }}</div>
              <ul class="q-my-none">
                <li v-for="(b, i) in blocking" :key="i">{{ t('groups.blockingItem', { ...b }) }}</li>
              </ul>
            </div>
            <div v-if="deleteError" class="text-negative" data-testid="group-delete-error">{{ deleteError }}</div>
          </q-card-section>
          <q-card-section v-else-if="impact.isError.value" class="text-negative">{{ t('groups.saveFailed') }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('groups.cancel')" data-testid="group-delete-cancel" @click="deleteTarget = null" />
            <q-btn
              color="negative" :label="t('groups.confirmDelete')" :loading="remove.isPending.value"
              :disable="!impact.data.value || blocking.length > 0" data-testid="group-delete-confirm" @click="confirmDelete"
            />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
