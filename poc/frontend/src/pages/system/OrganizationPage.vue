<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAdminUsers } from 'src/application/dynamic/queries/admin-queries';
import { useDepartments } from 'src/application/dynamic/queries/organization-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import DepartmentDetail from 'src/presentation/organization/DepartmentDetail.vue';
import DepartmentTree from 'src/presentation/organization/DepartmentTree.vue';
import { isForbidden } from 'src/services/api/http';

/** 系統管理 → 組織：桌機左樹右詳情；窄螢幕只有縮排清單，點部門開全螢幕詳情（設計 §11.5）。 */
const { t } = useI18n();
const $q = useQuasar();
const departments = useDepartments();
const users = useAdminUsers();
// 部門清單所有登入者都能讀，使用者清單只有系統管理員能讀：用它判斷這頁能不能用
const forbidden = computed(() => isForbidden(users.error.value));
const wide = computed(() => $q.screen.gt.sm);

/** null: nothing open; id: editing that department; { parentId }: a new department under parentId (null = root). */
const editing = ref<number | { parentId: number | null } | null>(null);
const list = computed(() => departments.data.value ?? []);
const current = computed(() => (typeof editing.value === 'number' ? list.value.find((d) => d.id === editing.value) ?? null : null));
const newParentId = computed(() => (editing.value !== null && typeof editing.value === 'object' ? editing.value.parentId : null));
const open = computed(() => editing.value !== null && (typeof editing.value === 'object' || current.value !== null));
const managerName = (userId: string) => users.data.value?.find((u) => u.userId === userId)?.displayName ?? userId;
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="users.isPending.value" class="row justify-center q-pa-lg"><q-spinner size="32px" color="primary" /></div>
    <template v-else>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('organization.title') }}</div>
        <q-space />
        <q-btn color="primary" icon="add" :label="t('organization.add')" data-testid="dept-add" @click="editing = { parentId: null }" />
      </div>
      <div class="row q-col-gutter-md">
        <div class="col-12 col-md-4">
          <q-card flat bordered>
            <DepartmentTree
              :departments="list" :selected-id="typeof editing === 'number' ? editing : null" :manager-name="managerName"
              @select="(id) => (editing = id)"
            />
          </q-card>
        </div>
        <div v-if="wide" class="col-12 col-md-8">
          <q-card flat bordered>
            <q-card-section v-if="open">
              <DepartmentDetail
                :department="current" :new-parent-id="newParentId" :departments="list" :users="users.data.value ?? []"
                @saved="(id) => (editing = id)" @deleted="editing = null" @add-child="(id) => (editing = { parentId: id })"
              />
            </q-card-section>
            <q-card-section v-else class="text-muted">{{ t('organization.selectHint') }}</q-card-section>
          </q-card>
        </div>
      </div>

      <q-dialog v-if="!wide" :model-value="open" maximized @update:model-value="editing = null">
        <q-card data-testid="dept-dialog">
          <q-bar>
            <div class="ellipsis">{{ t('organization.title') }}</div>
            <q-space />
            <q-btn flat dense icon="close" :aria-label="t('organization.close')" data-testid="dept-dialog-close" @click="editing = null" />
          </q-bar>
          <q-card-section>
            <DepartmentDetail
              :department="current" :new-parent-id="newParentId" :departments="list" :users="users.data.value ?? []"
              @saved="(id) => (editing = id)" @deleted="editing = null" @add-child="(id) => (editing = { parentId: id })"
            />
          </q-card-section>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
