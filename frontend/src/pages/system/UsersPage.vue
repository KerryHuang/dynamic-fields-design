<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useAdminUsers, useCreateUser, useResetPassword, useSetUserActive, useSetUserSystemPrivileges, useUpdateUser,
} from 'src/application/dynamic/queries/admin-queries';
import { useDepartments, useSetUserDepartment } from 'src/application/dynamic/queries/organization-queries';
import { useGroups } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { departmentPath } from 'src/domain/dynamic/organization';
import { copySystemPrivileges, noSystemPrivileges, sameSystemPrivileges, systemPrivilegeTags } from 'src/domain/dynamic/system-privileges';
import type { AdminUser, SystemPrivileges } from 'src/domain/dynamic/types';
import { leftoverMessages, notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import SystemPrivilegesEditor from 'src/presentation/dynamic/permissions/SystemPrivilegesEditor.vue';
import DepartmentSelect from 'src/presentation/organization/DepartmentSelect.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { joinList } from 'src/i18n/translate';

const MIN_PASSWORD = 8;
const { t, locale } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();
const users = useAdminUsers();
const groups = useGroups();
const departments = useDepartments();
const deptPath = (id: number | null | undefined) => departmentPath(departments.data.value ?? [], id, locale.value);
const filterDepartment = ref<number | null>(null);
const rows = computed(() =>
  (users.data.value ?? []).filter((u) => filterDepartment.value === null || u.departmentId === filterDepartment.value));
const forbidden = computed(() => isForbidden(users.error.value));

const groupOptions = computed(() => (groups.data.value ?? []).map((g) => ({ value: g.id, label: resolveI18n(g.name, locale.value) })));
const groupNames = (ids: number[]) =>
  joinList(ids.map((id) => groupOptions.value.find((o) => o.value === id)?.label).filter((l): l is string => !!l));

const columns = computed<QTableColumn<AdminUser>[]>(() => [
  { name: 'userId', label: t('users.userId'), align: 'left', field: 'userId' },
  { name: 'displayName', label: t('users.displayName'), align: 'left', field: 'displayName' },
  { name: 'groups', label: t('users.groups'), align: 'left', field: (u) => groupNames(u.groupIds) },
  { name: 'department', label: t('users.department'), align: 'left', field: (u) => deptPath(u.departmentId) },
  { name: 'status', label: t('users.status'), align: 'left', field: (u) => statusLabel(u) },
  { name: 'admin', label: t('users.systemAdmin'), align: 'left', field: (u) => (u.isSystemAdmin ? t('users.systemAdmin') : '') },
  // 有效結果（含群組、系統管理員自動擁有）；儲存格與卡片都畫成小標籤
  { name: 'systemPrivileges', label: t('systemPrivileges.title'), align: 'left', field: (u) => joinList(sysTags(u)) },
  { name: 'actions', label: '', align: 'right', field: 'userId' },
]);
const sysTags = (u: AdminUser) => systemPrivilegeTags(u.effectiveSystemPrivileges).map((k) => t(`systemPrivileges.${k}`));
const statusLabel = (u: AdminUser) => {
  if (!u.isActive) return t('users.inactive');
  return u.isLocked ? t('users.locked') : t('users.active');
};

const notifyFail = (e: unknown) => notifyApiError((o) => $q.notify(o), e as ApiError, [], t('users.saveFailed'));

// 新增／編輯共用一個對話框；編輯時帳號唯讀、不顯示密碼
const dialog = ref(false);
const editingId = ref<string | null>(null);
const form = ref({ userId: '', displayName: '', password: '', groupIds: [] as number[], isSystemAdmin: false, departmentId: null as number | null });
/** 已存進後端的帳號資料：系統權限失敗重試時，和它相同就不重送帳號（不多一筆 user.update 稽核）。 */
const savedMain = ref<{ displayName: string; groupIds: number[]; isSystemAdmin: boolean } | null>(null);
/** Department before editing: the department endpoint is only called when it changed. */
const originalDepartment = ref<number | null>(null);
/** 個人直接設定（不含經群組取得的）；和載入時相同就不送。 */
const privileges = ref<SystemPrivileges>(noSystemPrivileges());
const originalPrivileges = ref<SystemPrivileges>(noSystemPrivileges());
const editingUser = computed(() => (users.data.value ?? []).find((u) => u.userId === editingId.value));
const error = ref('');
const create = useCreateUser();
const update = useUpdateUser();
const setDepartment = useSetUserDepartment();
const setPrivileges = useSetUserSystemPrivileges();
const saving = computed(() => create.isPending.value || update.isPending.value || setDepartment.isPending.value || setPrivileges.isPending.value);

function openCreate() {
  editingId.value = null;
  form.value = { userId: '', displayName: '', password: '', groupIds: [], isSystemAdmin: false, departmentId: null };
  originalDepartment.value = null;
  savedMain.value = null;
  privileges.value = noSystemPrivileges();
  originalPrivileges.value = noSystemPrivileges();
  error.value = '';
  dialog.value = true;
}
function openEdit(u: AdminUser) {
  editingId.value = u.userId;
  form.value = {
    userId: u.userId, displayName: u.displayName, password: '', groupIds: [...u.groupIds], isSystemAdmin: u.isSystemAdmin,
    departmentId: u.departmentId ?? null,
  };
  originalDepartment.value = u.departmentId ?? null;
  savedMain.value = null;
  privileges.value = copySystemPrivileges(u.systemPrivileges);
  originalPrivileges.value = copySystemPrivileges(u.systemPrivileges);
  error.value = '';
  dialog.value = true;
}
function save() {
  error.value = '';
  const { userId, displayName, password, groupIds, isSystemAdmin, departmentId } = form.value;
  const showError = (e: unknown) => { error.value = joinList(leftoverMessages(e as ApiError, [])) || t('users.saveFailed'); };
  const done = () => { dialog.value = false; $q.notify({ type: 'positive', message: t('users.saved') }); };
  // 系統權限是最後一個呼叫（只在編輯時有）；失敗時帳號資料已存，提示只有系統權限沒存
  const sent = copySystemPrivileges(privileges.value);
  // 改自己：取消系統管理員後就沒有權限存系統權限，第二個請求必定 403；送出前攔下，請使用者分兩次存
  const privilegesChanged = !!editingId.value && !sameSystemPrivileges(sent, originalPrivileges.value);
  if (editingId.value === identity.userId && editingUser.value?.isSystemAdmin && !isSystemAdmin && privilegesChanged) {
    error.value = t('systemPrivileges.selfDemoteConflict');
    return;
  }
  const savePrivileges = () => {
    if (!editingId.value || sameSystemPrivileges(sent, originalPrivileges.value)) { done(); return; }
    setPrivileges.mutate({ id: userId, body: sent }, {
      onSuccess: () => { originalPrivileges.value = sent; done(); },
      onError: (e) => {
        error.value = t('systemPrivileges.partialSaved', { reason: joinList(leftoverMessages(e as unknown as ApiError, [])) || t('users.saveFailed') });
      },
    });
  };
  // The account is saved first; its department is a separate call, so a department error leaves the dialog open
  const handlers = {
    onSuccess: () => {
      if (departmentId === originalDepartment.value) { savePrivileges(); return; }
      setDepartment.mutate({ userId, departmentId }, {
        onSuccess: () => { originalDepartment.value = departmentId; savePrivileges(); },
        onError: (e) => { if (!editingId.value) editingId.value = userId; showError(e); },
      });
    },
    onError: showError,
  };
  if (!editingId.value) { create.send({ userId, displayName, groupIds, isSystemAdmin }, { password }, handlers); return; }
  const saved = savedMain.value;
  const mainUnchanged = !!saved && saved.displayName === displayName && saved.isSystemAdmin === isSystemAdmin
    && saved.groupIds.length === groupIds.length && saved.groupIds.every((id, i) => id === groupIds[i]);
  if (mainUnchanged) { handlers.onSuccess(); return; }
  const main = { displayName, groupIds: [...groupIds], isSystemAdmin };
  update.mutate({ id: editingId.value, body: main }, { onSuccess: () => { savedMain.value = main; handlers.onSuccess(); }, onError: showError });
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
  reset.send({ id: resetTarget.value! }, resetPassword.value, {
    onSuccess: () => { resetTarget.value = null; $q.notify({ type: 'positive', message: t('users.passwordReset') }); },
    onError: (e) => { resetError.value = joinList(leftoverMessages(e as unknown as ApiError, [])) || t('users.saveFailed'); },
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
      <div class="row q-mb-md">
        <DepartmentSelect
          v-model="filterDepartment" :label="t('users.filterDepartment')" class="col-12 col-sm-6 col-md-4"
          data-testid="user-filter-department"
        />
      </div>
      <q-table
        flat bordered row-key="userId" :rows="rows" :columns="columns" :loading="users.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination :no-data-label="t('dynamic.noData')" :grid="$q.screen.lt.sm"
      >
        <template #body="{ row, cols }">
          <q-tr
            class="row-clickable" tabindex="0" :data-testid="`user-row-${row.userId}`"
            @click="openEdit(row)" @keydown.enter.self.prevent="openEdit(row)" @keydown.space.self.prevent="openEdit(row)"
          >
            <template v-for="c in cols" :key="c.name">
              <q-td v-if="c.name === 'actions'" :align="c.align" @click.stop>
                <q-btn flat dense color="primary" :label="t('users.resetPassword')" :data-testid="`user-reset-${row.userId}`" @click="openReset(row)" />
                <q-btn
                  flat dense :color="row.isActive ? 'negative' : 'primary'" :label="row.isActive ? t('users.deactivate') : t('users.activate')"
                  :disable="row.userId === identity.userId" :data-testid="`user-toggle-${row.userId}`" @click="toggle(row)"
                />
              </q-td>
              <q-td v-else-if="c.name === 'systemPrivileges'" :align="c.align" :data-testid="`user-sp-${row.userId}`">
                <q-badge v-for="tag in sysTags(row)" :key="tag" outline color="primary" class="q-mr-xs" :label="tag" />
              </q-td>
              <q-td v-else :align="c.align">{{ c.value }}</q-td>
            </template>
          </q-tr>
        </template>
        <template #item="{ row, cols }">
          <div class="col-12 q-py-xs">
            <q-card
              flat bordered class="cursor-pointer card-clickable" tabindex="0" :data-testid="`user-card-${row.userId}`"
              @click="openEdit(row)" @keydown.enter.self.prevent="openEdit(row)" @keydown.space.self.prevent="openEdit(row)"
            >
              <q-list dense>
                <q-item v-for="c in cols.filter((x: { name: string; value: unknown }) => x.name !== 'actions' && x.value)" :key="c.name">
                  <q-item-section>
                    <q-item-label caption>{{ c.label }}</q-item-label>
                    <q-item-label v-if="c.name === 'systemPrivileges'" :data-testid="`user-sp-${row.userId}`">
                      <q-badge v-for="tag in sysTags(row)" :key="tag" outline color="primary" class="q-mr-xs" :label="tag" />
                    </q-item-label>
                    <q-item-label v-else style="overflow-wrap: anywhere">{{ c.value }}</q-item-label>
                  </q-item-section>
                </q-item>
              </q-list>
              <q-card-actions align="right" @click.stop>
                <q-btn flat dense color="primary" :label="t('users.resetPassword')" :data-testid="`user-reset-${row.userId}`" @click="openReset(row)" />
                <q-btn
                  flat dense :color="row.isActive ? 'negative' : 'primary'" :label="row.isActive ? t('users.deactivate') : t('users.activate')"
                  :disable="row.userId === identity.userId" :data-testid="`user-toggle-${row.userId}`" @click="toggle(row)"
                />
              </q-card-actions>
            </q-card>
          </div>
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
            <div class="col-12 col-sm-6">
              <DepartmentSelect v-model="form.departmentId" :label="t('users.department')" assignable data-testid="user-department" />
            </div>
            <div class="col-12 col-sm-6 self-center">
              <q-toggle v-model="form.isSystemAdmin" :label="t('users.systemAdmin')" data-testid="user-admin" />
            </div>
            <div v-if="editingId" class="col-12">
              <SystemPrivilegesEditor
                v-model="privileges" :sources="editingUser?.systemPrivilegeSources" :system-admin="form.isSystemAdmin"
              />
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
