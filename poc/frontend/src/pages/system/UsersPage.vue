<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAdminUsers, useCreateUser, useResetPassword, useSetUserActive, useUpdateUser } from 'src/application/dynamic/queries/admin-queries';
import { useGroups } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { AdminUser } from 'src/domain/dynamic/types';
import { leftoverMessages, notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

const MIN_PASSWORD = 8;
const { t, locale } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();
const users = useAdminUsers();
const groups = useGroups();
const forbidden = computed(() => isForbidden(users.error.value));

const groupOptions = computed(() => (groups.data.value ?? []).map((g) => ({ value: g.id, label: resolveI18n(g.name, locale.value) })));
const groupNames = (ids: number[]) =>
  ids.map((id) => groupOptions.value.find((o) => o.value === id)?.label).filter(Boolean).join('、');

const columns = computed<QTableColumn<AdminUser>[]>(() => [
  { name: 'userId', label: t('users.userId'), align: 'left', field: 'userId' },
  { name: 'displayName', label: t('users.displayName'), align: 'left', field: 'displayName' },
  { name: 'groups', label: t('users.groups'), align: 'left', field: (u) => groupNames(u.groupIds) },
  { name: 'status', label: t('users.status'), align: 'left', field: (u) => statusLabel(u) },
  { name: 'admin', label: t('users.systemAdmin'), align: 'left', field: (u) => (u.isSystemAdmin ? t('users.systemAdmin') : '') },
  { name: 'actions', label: '', align: 'right', field: 'userId' },
]);
const statusLabel = (u: AdminUser) => {
  if (!u.isActive) return t('users.inactive');
  return u.isLocked ? t('users.locked') : t('users.active');
};

const notifyFail = (e: unknown) => notifyApiError((o) => $q.notify(o), e as ApiError, [], t('users.saveFailed'));

// 新增／編輯共用一個對話框；編輯時帳號唯讀、不顯示密碼
const dialog = ref(false);
const editingId = ref<string | null>(null);
const form = ref({ userId: '', displayName: '', password: '', groupIds: [] as number[], isSystemAdmin: false });
const error = ref('');
const create = useCreateUser();
const update = useUpdateUser();
const saving = computed(() => create.isPending.value || update.isPending.value);

function openCreate() {
  editingId.value = null;
  form.value = { userId: '', displayName: '', password: '', groupIds: [], isSystemAdmin: false };
  error.value = '';
  dialog.value = true;
}
function openEdit(u: AdminUser) {
  editingId.value = u.userId;
  form.value = { userId: u.userId, displayName: u.displayName, password: '', groupIds: [...u.groupIds], isSystemAdmin: u.isSystemAdmin };
  error.value = '';
  dialog.value = true;
}
function save() {
  error.value = '';
  const { userId, displayName, password, groupIds, isSystemAdmin } = form.value;
  const handlers = {
    onSuccess: () => { dialog.value = false; $q.notify({ type: 'positive', message: t('users.saved') }); },
    onError: (e: unknown) => { error.value = leftoverMessages(e as ApiError, []).join('、') || t('users.saveFailed'); },
  };
  if (editingId.value) update.mutate({ id: editingId.value, body: { displayName, groupIds, isSystemAdmin } }, handlers);
  else create.mutate({ userId, displayName, password, groupIds, isSystemAdmin }, handlers);
}

const resetTarget = ref<string | null>(null);
const resetPassword = ref('');
const resetError = ref('');
const reset = useResetPassword();
function openReset(u: AdminUser) {
  resetTarget.value = u.userId;
  resetPassword.value = '';
  resetError.value = '';
}
function submitReset() {
  if (resetPassword.value.length < MIN_PASSWORD) { resetError.value = t('users.passwordTooShort'); return; }
  reset.mutate({ id: resetTarget.value!, password: resetPassword.value }, {
    onSuccess: () => { resetTarget.value = null; $q.notify({ type: 'positive', message: t('users.passwordReset') }); },
    onError: (e) => { resetError.value = leftoverMessages(e as unknown as ApiError, []).join('、') || t('users.saveFailed'); },
  });
}

const deactivateTarget = ref<AdminUser | null>(null);
const setActive = useSetUserActive();
function toggle(u: AdminUser) {
  if (u.isActive) deactivateTarget.value = u;
  else runSetActive(u, true);
}
function runSetActive(u: AdminUser, active: boolean) {
  setActive.mutate({ id: u.userId, active }, {
    onSuccess: () => { deactivateTarget.value = null; },
    onError: (e) => { deactivateTarget.value = null; notifyFail(e); },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('users.title') }}</div>
        <q-space />
        <q-btn color="primary" icon="add" :label="t('users.create')" data-testid="user-create" @click="openCreate" />
      </div>
      <q-table
        flat bordered row-key="userId" :rows="users.data.value ?? []" :columns="columns" :loading="users.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination
      >
        <template #body="{ row, cols }">
          <q-tr :data-testid="`user-row-${row.userId}`">
            <q-td v-for="c in cols" :key="c.name" :align="c.align">
              <template v-if="c.name === 'actions'">
                <q-btn flat dense color="primary" :label="t('users.edit')" :data-testid="`user-edit-${row.userId}`" @click="openEdit(row)" />
                <q-btn flat dense color="primary" :label="t('users.resetPassword')" :data-testid="`user-reset-${row.userId}`" @click="openReset(row)" />
                <q-btn
                  flat dense :color="row.isActive ? 'negative' : 'primary'" :label="row.isActive ? t('users.deactivate') : t('users.activate')"
                  :disable="row.userId === identity.userId" :data-testid="`user-toggle-${row.userId}`" @click="toggle(row)"
                />
              </template>
              <template v-else>{{ c.value }}</template>
            </q-td>
          </q-tr>
        </template>
      </q-table>

      <q-dialog v-model="dialog" @hide="form.password = ''">
        <q-card style="width: 640px; max-width: 95vw">
          <q-card-section class="text-h6">{{ editingId ? t('users.edit') : t('users.create') }}</q-card-section>
          <q-card-section class="row q-col-gutter-md">
            <div class="col-12 col-sm-6">
              <q-input v-model="form.userId" :label="t('users.userId')" :hint="t('users.userIdHint')" :readonly="!!editingId" dense outlined data-testid="user-id" />
            </div>
            <div class="col-12 col-sm-6">
              <q-input v-model="form.displayName" :label="t('users.displayName')" dense outlined data-testid="user-name" />
            </div>
            <div v-if="!editingId" class="col-12 col-sm-6">
              <q-input v-model="form.password" :label="t('users.password')" type="password" autocomplete="new-password" dense outlined data-testid="user-password" />
            </div>
            <div class="col-12 col-sm-6">
              <q-select
                v-model="form.groupIds" :options="groupOptions" :label="t('users.groups')" multiple use-chips emit-value map-options dense outlined
                data-testid="user-groups"
              />
            </div>
            <div class="col-12 col-sm-6 self-center">
              <q-toggle v-model="form.isSystemAdmin" :label="t('users.systemAdmin')" data-testid="user-admin" />
            </div>
            <div v-if="error" class="col-12 text-negative" data-testid="user-error">{{ error }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('users.cancel')" data-testid="user-cancel" @click="dialog = false" />
            <q-btn color="primary" :label="t('users.save')" :loading="saving" data-testid="user-save" @click="save" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="resetTarget !== null" @update:model-value="resetTarget = null" @hide="resetPassword = ''">
        <q-card style="width: 400px; max-width: 95vw">
          <q-card-section class="text-h6">{{ t('users.resetPassword') }}：{{ resetTarget }}</q-card-section>
          <q-card-section>
            <q-input
              v-model="resetPassword" :label="t('users.password')" type="password" autocomplete="new-password" dense outlined
              :error="!!resetError" :error-message="resetError" data-testid="user-reset-password"
            />
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('users.cancel')" data-testid="user-reset-cancel" @click="resetTarget = null" />
            <q-btn color="primary" :label="t('users.save')" :loading="reset.isPending.value" data-testid="user-reset-submit" @click="submitReset" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="deactivateTarget !== null" @update:model-value="deactivateTarget = null">
        <q-card style="width: 400px; max-width: 95vw">
          <q-card-section>{{ t('users.confirmDeactivate', { name: deactivateTarget?.displayName ?? '' }) }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('users.cancel')" @click="deactivateTarget = null" />
            <q-btn color="negative" :label="t('users.deactivate')" data-testid="user-confirm-ok" @click="runSetActive(deactivateTarget!, false)" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
