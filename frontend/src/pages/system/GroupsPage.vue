<script setup lang="ts">
import { useQuasar, type QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useAdminGroups, useAdminUsers, useCreateGroup, useDeleteGroup, useGroupImpact, useRenameGroup, useSetGroupMembers, useSetGroupSystemPrivileges,
} from 'src/application/dynamic/queries/admin-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import {
  copySystemPrivileges, designerActions, noSystemPrivileges, sameSystemPrivileges, systemPrivilegeTags,
} from 'src/domain/dynamic/system-privileges';
import type { AdminGroup, BlockingOption, SystemPrivileges } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import SystemPrivilegesEditor from 'src/presentation/dynamic/permissions/SystemPrivilegesEditor.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';

const { t, locale } = useI18n();
const $q = useQuasar();
const groups = useAdminGroups();
const users = useAdminUsers();
const forbidden = computed(() => isForbidden(groups.error.value));
const nameOf = (g: AdminGroup) => resolveI18n(g.nameI18n, locale.value);
const sysTags = (g: AdminGroup) => systemPrivilegeTags(g.systemPrivileges).map((k) => t(`systemPrivileges.${k}`));
const errorText = (e: unknown, fallback = t('groups.saveFailed')) => joinList(leftoverMessages(e as ApiError, [])) || fallback;

const columns = computed<QTableColumn<AdminGroup>[]>(() => [
  { name: 'name', label: t('groups.name'), align: 'left', field: nameOf },
  { name: 'count', label: t('groups.memberCount'), align: 'left', field: (g) => g.memberUserIds.length },
  // 直接設定（群組列本身就是來源）
  { name: 'systemPrivileges', label: t('systemPrivileges.title'), align: 'left', field: (g) => joinList(sysTags(g)) },
  { name: 'actions', label: '', align: 'right', field: 'id' },
]);

// 新增／編輯共用一個對話框：編輯時名稱（只改目前語系）與成員在同一處改，新增只填名稱。
// 成員只列啟用中的使用者；已在群組內但已停用的人保留勾選，儲存時不會被默默移除
const dialog = ref(false);
const editing = ref<AdminGroup | null>(null);
const name = ref('');
// 已存進後端的名稱：改名成功後更新，成員失敗重試時不再重送改名
const savedName = ref('');
const selected = ref<string[]>([]);
const formError = ref('');
const privileges = ref<SystemPrivileges>(noSystemPrivileges());
/** 已存進後端的系統權限：和它相同就不送，系統權限失敗重試時也以它比較。 */
const savedPrivileges = ref<SystemPrivileges>(noSystemPrivileges());
const create = useCreateGroup();
const rename = useRenameGroup();
const setMembers = useSetGroupMembers();
const setPrivileges = useSetGroupSystemPrivileges();
const saving = computed(() => create.isPending.value || rename.isPending.value || setMembers.isPending.value || setPrivileges.isPending.value);
const memberOptions = computed(() => {
  const inGroup = new Set(editing.value?.memberUserIds ?? []);
  return (users.data.value ?? []).filter((u) => u.isActive || inGroup.has(u.userId));
});
function openEditor(g?: AdminGroup) {
  editing.value = g ?? null;
  name.value = g ? nameOf(g) : '';
  savedName.value = name.value;
  selected.value = g ? [...g.memberUserIds] : [];
  privileges.value = copySystemPrivileges(g?.systemPrivileges);
  savedPrivileges.value = copySystemPrivileges(g?.systemPrivileges);
  formError.value = '';
  dialog.value = true;
}
function save() {
  formError.value = '';
  const g = editing.value;
  const done = () => { dialog.value = false; $q.notify({ type: 'positive', message: t('groups.saved') }); };
  const fail = (e: unknown) => { formError.value = errorText(e); };
  if (!g) { create.mutate({ name: name.value, locale: locale.value }, { onSuccess: done, onError: fail }); return; }
  // 系統權限最後送；失敗時名稱與成員已存，提示只有系統權限沒存
  const sent = copySystemPrivileges(privileges.value);
  const savePrivileges = () => {
    if (sameSystemPrivileges(sent, savedPrivileges.value)) { done(); return; }
    setPrivileges.mutate({ id: g.id, body: sent }, {
      onSuccess: () => { savedPrivileges.value = sent; done(); },
      onError: (e) => { formError.value = t('systemPrivileges.partialSaved', { reason: errorText(e) }); },
    });
  };
  // 名稱先存；成員是另一個呼叫，名稱出錯時對話框保持開啟
  const saveMembers = () => {
    const unchanged = selected.value.length === g.memberUserIds.length && selected.value.every((id) => g.memberUserIds.includes(id));
    if (unchanged) { savePrivileges(); return; }
    setMembers.mutate({ id: g.id, userIds: selected.value }, { onSuccess: savePrivileges, onError: fail });
  };
  if (name.value === savedName.value) saveMembers();
  else {
    const sent = name.value;
    rename.mutate({ id: g.id, name: sent, locale: locale.value }, { onSuccess: () => { savedName.value = sent; saveMembers(); }, onError: fail });
  }
}

// 刪除：先查影響；有選項範圍指定時停用刪除，409（別人剛加了範圍）把清單換成後端回的
const deleteTarget = ref<AdminGroup | null>(null);
const conflictOptions = ref<BlockingOption[] | null>(null);
const deleteError = ref('');
const impact = useGroupImpact(computed(() => deleteTarget.value?.id ?? null));
const remove = useDeleteGroup();
/** 刪除會移除的系統權限：「設計器（讀取、修改）、權限設定」，沒有就是空字串。 */
const impactPrivileges = computed(() => {
  const p = impact.data.value?.systemPrivileges;
  if (!p) return '';
  const actions = designerActions(p.designer);
  return joinList([
    ...(actions.length ? [t('systemPrivileges.designerWith', { actions: joinList(actions.map((a) => t(`designer.permissions.axes.${a}`))) })] : []),
    ...(p.permissionAdmin ? [t('systemPrivileges.permissionAdmin')] : []),
  ]);
});
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
        <q-btn color="primary" icon="add" :label="t('groups.create')" data-testid="group-create" @click="openEditor()" />
      </div>
      <q-table
        flat bordered row-key="id" :rows="groups.data.value ?? []" :columns="columns" :loading="groups.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination :no-data-label="t('dynamic.noData')" :grid="$q.screen.lt.sm"
      >
        <template #body="{ row, cols }">
          <q-tr
            class="row-clickable" tabindex="0" :data-testid="`group-row-${row.id}`"
            @click="openEditor(row)" @keydown.enter.self.prevent="openEditor(row)" @keydown.space.self.prevent="openEditor(row)"
          >
            <template v-for="c in cols" :key="c.name">
              <q-td v-if="c.name === 'actions'" :align="c.align" @click.stop>
                <q-btn flat dense color="negative" :label="t('groups.delete')" :data-testid="`group-delete-${row.id}`" @click="openDelete(row)" />
              </q-td>
              <q-td v-else-if="c.name === 'systemPrivileges'" :align="c.align" :data-testid="`group-sp-${row.id}`">
                <q-badge v-for="tag in sysTags(row)" :key="tag" outline color="primary" class="q-mr-xs" :label="tag" />
              </q-td>
              <q-td v-else :align="c.align">{{ c.value }}</q-td>
            </template>
          </q-tr>
        </template>
        <template #item="{ row, cols }">
          <div class="col-12 q-py-xs">
            <q-card
              flat bordered class="cursor-pointer card-clickable" tabindex="0" :data-testid="`group-card-${row.id}`"
              @click="openEditor(row)" @keydown.enter.self.prevent="openEditor(row)" @keydown.space.self.prevent="openEditor(row)"
            >
              <q-list dense>
                <q-item v-for="c in cols.filter((x: { name: string; value: unknown }) => x.name !== 'actions' && x.value !== '')" :key="c.name">
                  <q-item-section>
                    <q-item-label caption>{{ c.label }}</q-item-label>
                    <q-item-label v-if="c.name === 'systemPrivileges'" :data-testid="`group-sp-${row.id}`">
                      <q-badge v-for="tag in sysTags(row)" :key="tag" outline color="primary" class="q-mr-xs" :label="tag" />
                    </q-item-label>
                    <q-item-label v-else style="overflow-wrap: anywhere">{{ c.value }}</q-item-label>
                  </q-item-section>
                </q-item>
              </q-list>
              <q-card-actions align="right" @click.stop>
                <q-btn flat dense color="negative" :label="t('groups.delete')" :data-testid="`group-delete-${row.id}`" @click="openDelete(row)" />
              </q-card-actions>
            </q-card>
          </div>
        </template>
      </q-table>

      <q-dialog v-model="dialog">
        <q-card style="width: 640px; max-width: 95vw">
          <q-card-section class="text-h6">{{ editing ? t('groups.edit') : t('groups.create') }}</q-card-section>
          <q-card-section class="row q-col-gutter-md">
            <div class="col-12">
              <q-input v-model="name" :label="t('groups.name')" dense outlined data-testid="group-name" @keyup.enter="save" />
            </div>
            <template v-if="editing">
              <div class="col-12 text-subtitle2">{{ t('groups.members') }}</div>
              <div class="col-12 row q-col-gutter-sm" style="max-height: 50vh; overflow: auto">
                <div v-for="u in memberOptions" :key="u.userId" class="col-12 col-sm-6">
                  <q-checkbox
                    v-model="selected" :val="u.userId" dense :data-testid="`member-${u.userId}`"
                    :label="u.isActive ? `${u.displayName} (${u.userId})` : `${u.displayName} (${u.userId}) ${t('groups.inactive')}`"
                  />
                </div>
              </div>
            </template>
            <div v-if="editing" class="col-12">
              <SystemPrivilegesEditor v-model="privileges" />
            </div>
            <div v-if="formError" class="col-12 text-negative" data-testid="group-error">{{ formError }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('groups.cancel')" data-testid="group-cancel" @click="dialog = false" />
            <q-btn color="primary" :label="t('groups.save')" :loading="saving" data-testid="group-save" @click="save" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="deleteTarget !== null" @update:model-value="deleteTarget = null">
        <q-card style="width: 520px; max-width: 95vw">
          <q-card-section class="text-h6">{{ t('groups.deleteTitle', { name: deleteTarget ? nameOf(deleteTarget) : '' }) }}</q-card-section>
          <q-card-section v-if="impact.data.value" class="column q-gutter-y-sm">
            <div data-testid="impact-members">{{ t('groups.impactMembers', { n: impact.data.value.memberCount }) }}</div>
            <div data-testid="impact-modules">
              {{ t('groups.impactModules', { list: joinList(impact.data.value.modules) || t('groups.impactNone') }) }}
            </div>
            <div data-testid="impact-fields">{{ t('groups.impactFields', { n: impact.data.value.fieldPermissionCount }) }}</div>
            <div data-testid="impact-scopes">{{ t('groups.impactScopes', { n: impact.data.value.dataScopeCount }) }}</div>
            <div data-testid="impact-reports">
              {{ t('groups.impactReports', { list: joinList(impact.data.value.reports) || t('groups.impactNone') }) }}
            </div>
            <div data-testid="impact-dashboards">
              {{ t('groups.impactDashboards', { list: joinList(impact.data.value.dashboards) || t('groups.impactNone') }) }}
            </div>
            <div v-if="impactPrivileges" class="text-warning" data-testid="impact-system-privileges">
              {{ t('systemPrivileges.deleteImpact', { list: impactPrivileges }) }}
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
