<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useCreateDepartment, useDeleteDepartment, useUpdateDepartment } from 'src/application/dynamic/queries/organization-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import {
  buildDepartmentTree, departmentPath, descendantIds, flattenDepartmentTree, type Department, type DepartmentInput,
} from 'src/domain/dynamic/organization';
import type { AdminUser, I18nText } from 'src/domain/dynamic/types';
import { errorFor, notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import type { ApiError } from 'src/services/api/http';

/** Detail and edit form of one department; `department` null means a new one under `newParentId`. */
const props = defineProps<{ department: Department | null; newParentId: number | null; departments: Department[]; users: AdminUser[] }>();
const emit = defineEmits<{ saved: [id: number]; deleted: []; addChild: [parentId: number] }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const FIELDS = ['code', 'name', 'parentId', 'managerUserId'];
const form = ref({ code: '', name: {} as I18nText, managerUserId: null as string | null, sortOrder: 0, active: true });
const apiError = ref<ApiError | null>(null);
watch(() => [props.department, props.newParentId], () => {
  const d = props.department;
  form.value = d
    ? { code: d.code, name: { ...d.name }, managerUserId: d.managerUserId, sortOrder: d.sortOrder, active: d.active }
    : { code: '', name: {}, managerUserId: null, sortOrder: 0, active: true };
  apiError.value = null;
}, { immediate: true });

// 表單與已儲存的值不同：搬移只送已儲存的值，搬完表單會重設，未存的修改會消失，所以先擋下
const dirty = computed(() => {
  const d = props.department;
  if (!d) return false;
  const f = form.value;
  return f.code !== d.code || f.managerUserId !== d.managerUserId || f.sortOrder !== d.sortOrder || f.active !== d.active
    || JSON.stringify(f.name) !== JSON.stringify(d.name);
});

const parentId = computed(() => (props.department ? props.department.parentId : props.newParentId));
const parentPath = computed(() => departmentPath(props.departments, parentId.value, locale.value) || t('organization.root'));
const nameOf = (userId: string) => props.users.find((u) => u.userId === userId)?.displayName ?? userId;
const managerOptions = computed(() =>
  props.users.filter((u) => u.isActive || u.userId === props.department?.managerUserId)
    .map((u) => ({ value: u.userId, label: `${u.displayName} (${u.userId})` })));
const children = computed(() => (props.department ? props.departments.filter((d) => d.parentId === props.department!.id).length : 0));
const members = computed(() => props.department?.memberUserIds.length ?? 0);
const blocked = computed(() => children.value + members.value > 0);

const create = useCreateDepartment();
const update = useUpdateDepartment();
const remove = useDeleteDepartment();
const saving = computed(() => create.isPending.value || update.isPending.value);

function fail(e: unknown) {
  apiError.value = e as ApiError;
  notifyApiError((o) => $q.notify(o), e as ApiError, FIELDS, t('organization.saveFailed'));
}
const ok = (id: number) => { $q.notify({ type: 'positive', message: t('organization.saved') }); emit('saved', id); };

function save() {
  apiError.value = null;
  const body: DepartmentInput = { ...form.value, code: form.value.code.trim(), parentId: parentId.value };
  if (props.department) update.mutate({ id: props.department.id, body }, { onSuccess: () => ok(props.department!.id), onError: fail });
  else create.mutate(body, { onSuccess: (d) => ok((d as Department).id), onError: fail });
}

// 「移到…」：選項排除自己與所有下級；搬移只改上級，其他欄位用已儲存的值
const moveOpen = ref(false);
const moveOptions = computed(() => {
  const d = props.department;
  if (!d) return [];
  const excluded = descendantIds(props.departments, d.id).add(d.id);
  return flattenDepartmentTree(buildDepartmentTree(props.departments)).filter((n) => !excluded.has(n.id));
});
function moveTo(target: number | null) {
  const d = props.department!;
  moveOpen.value = false;
  const body: DepartmentInput = {
    code: d.code, name: d.name, parentId: target, managerUserId: d.managerUserId, sortOrder: d.sortOrder, active: d.active,
  };
  update.mutate({ id: d.id, body }, { onSuccess: () => ok(d.id), onError: fail });
}

const confirmDelete = ref(false);
function runDelete() {
  confirmDelete.value = false;
  remove.mutate(props.department!.id, {
    onSuccess: () => { $q.notify({ type: 'positive', message: t('organization.deleted') }); emit('deleted'); },
    onError: fail,
  });
}
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="dept-detail">
    <div class="row q-col-gutter-md">
      <div class="col-12 col-sm-6">
        <q-input
          v-model="form.code" :label="`${t('organization.code')} *`" :hint="t('organization.codeHint')" dense outlined
          :error="!!errorFor(apiError, 'code')" :error-message="errorFor(apiError, 'code')" data-testid="dept-code"
        />
      </div>
      <div class="col-12 col-sm-6">
        <I18nTextInput v-model="form.name" :label="t('organization.name')" :error="errorFor(apiError, 'name')" />
      </div>
      <div class="col-12 col-sm-6">
        <q-select
          v-model="form.managerUserId" :options="managerOptions" :label="t('organization.manager')" emit-value map-options clearable
          dense outlined :hint="form.managerUserId ? undefined : t('organization.vacant')"
          :display-value="form.managerUserId ? nameOf(form.managerUserId) : ''"
          :error="!!errorFor(apiError, 'managerUserId')" :error-message="errorFor(apiError, 'managerUserId')" data-testid="dept-manager"
        />
      </div>
      <div class="col-6 col-sm-3">
        <q-input v-model.number="form.sortOrder" type="number" :label="t('organization.sortOrder')" dense outlined data-testid="dept-sort" />
      </div>
      <div class="col-6 col-sm-3 self-center">
        <q-toggle v-model="form.active" :label="t('organization.active')" data-testid="dept-active" />
      </div>
      <div class="col-12">
        <div class="text-caption text-muted">{{ t('organization.parent') }}</div>
        <div class="row items-center q-gutter-sm">
          <div class="ellipsis" style="min-width: 0; flex: 1 1 auto" data-testid="dept-parent">{{ parentPath }}</div>
          <q-btn
            v-if="department" flat dense color="primary" icon="drive_file_move" :label="t('organization.moveTo')" :disable="dirty"
            data-testid="dept-move" @click="moveOpen = true"
          />
        </div>
        <div v-if="dirty" class="text-caption text-muted" data-testid="dept-move-hint">{{ t('organization.moveBlockedDirty') }}</div>
        <div v-if="errorFor(apiError, 'parentId')" class="text-negative text-caption">{{ errorFor(apiError, 'parentId') }}</div>
      </div>
    </div>

    <div v-if="department">
      <div class="text-subtitle2">{{ t('organization.members') }}</div>
      <div data-testid="dept-members">
        <q-chip v-for="m in department.memberUserIds" :key="m" dense>{{ nameOf(m) }}</q-chip>
        <span v-if="!department.memberUserIds.length" class="text-muted">{{ t('organization.noMembers') }}</span>
      </div>
    </div>

    <div v-if="department && blocked" class="text-caption text-muted" data-testid="dept-delete-blocked">
      {{ t('organization.deleteBlocked', { children, members }) }}
    </div>
    <div class="row q-gutter-sm justify-end">
      <q-btn
        v-if="department" flat color="negative" :label="t('organization.delete')" :disable="blocked" :loading="remove.isPending.value"
        data-testid="dept-delete" @click="confirmDelete = true"
      />
      <q-btn v-if="department" flat color="primary" icon="add" :label="t('organization.addChild')" data-testid="dept-add-child" @click="emit('addChild', department.id)" />
      <q-btn color="primary" :label="t('organization.save')" :loading="saving" data-testid="dept-save" @click="save" />
    </div>

    <q-dialog v-model="moveOpen">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('organization.moveTitle', { name: department ? resolveI18n(department.name, locale) : '' }) }}</q-card-section>
        <q-list separator style="max-height: 60vh; overflow: auto">
          <q-item v-if="department?.parentId !== null" clickable data-testid="move-option-root" @click="moveTo(null)">
            <q-item-section>{{ t('organization.root') }}</q-item-section>
          </q-item>
          <q-item v-for="n in moveOptions" :key="n.id" clickable :data-testid="`move-option-${n.id}`" @click="moveTo(n.id)">
            <q-item-section :style="{ paddingLeft: `${Math.min(n.depth, 9) * 16}px` }">{{ resolveI18n(n.name, locale) }}</q-item-section>
          </q-item>
        </q-list>
        <q-card-actions align="right">
          <q-btn flat :label="t('organization.cancel')" @click="moveOpen = false" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog v-model="confirmDelete">
      <q-card style="width: 400px; max-width: 95vw">
        <q-card-section>{{ t('organization.confirmDelete', { name: department ? resolveI18n(department.name, locale) : '' }) }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('organization.cancel')" @click="confirmDelete = false" />
          <q-btn color="negative" :label="t('organization.delete')" data-testid="dept-delete-confirm" @click="runDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
