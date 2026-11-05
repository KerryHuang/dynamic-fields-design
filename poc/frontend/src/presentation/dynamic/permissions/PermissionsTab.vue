<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useGroups, useMe, useModulePermissions, useSaveModulePermissions, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import type { DesignerField, ModulePermissions } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import FieldPermissionEditor from 'src/presentation/dynamic/permissions/FieldPermissionEditor.vue';
import PrivilegeEditor from 'src/presentation/dynamic/permissions/PrivilegeEditor.vue';
import type { ApiError } from 'src/services/api/http';

const props = withDefaults(defineProps<{ moduleKey: string; fields?: DesignerField[]; titleFieldId?: number; detailOf?: string }>(), { fields: () => [], titleFieldId: undefined, detailOf: undefined });
const { t } = useI18n();
const $q = useQuasar();

const permsQuery = useModulePermissions(() => props.moduleKey);
const groups = useGroups();
const users = useUsers();
const me = useMe();
const save = useSaveModulePermissions(() => props.moduleKey);

// Keep the whole object (fields/options included) so the PUT replaces nothing unintentionally.
const draft = ref<ModulePermissions | null>(null);
const errors = ref<Record<string, string[]>>({});
// data.value is a readonly reactive proxy (structuredClone throws on it); the data is plain JSON.
// Following data (not just seeding) also drops the previous module's draft when moduleKey changes.
watch(() => permsQuery.data.value, (d) => {
  draft.value = d ? JSON.parse(JSON.stringify(d)) : null;
  errors.value = {};
}, { immediate: true });

const readonly = computed(() => !me.data.value?.designer.edit);
// Error keys index into the array that was sent; any edit reshuffles rows, so marks must not outlive it.
const draftModel = computed({
  get: () => draft.value!,
  set: (v: ModulePermissions) => { draft.value = v; errors.value = {}; },
});
const ready = computed(() => draft.value && groups.data.value && users.data.value && me.data.value);

function submit() {
  if (!draft.value) return;
  errors.value = {};
  // A detail module inherits the header's privileges; the server only accepts field exceptions and options.
  const body = props.detailOf ? { ...draft.value, groups: [], users: [] } : draft.value;
  save.mutate(body, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('designer.permissions.saved') }),
    onError: (e) => {
      const err = e as unknown as ApiError;
      errors.value = err.errors ?? {};
      const rest = leftoverMessages(err, ['groups[', 'users[', 'fields[', 'options[']);
      $q.notify({ type: 'negative', message: rest.length ? rest.join('、') : t('designer.saveFailed') });
    },
  });
}
</script>

<template>
  <div v-if="permsQuery.isError.value" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
  <div v-else-if="!ready" class="text-grey">{{ t('designer.loading') }}</div>
  <div v-else>
    <div v-if="detailOf" class="text-body2 text-grey-8" data-testid="detail-inherited">{{ t('designer.permissions.detailInherited') }}</div>
    <PrivilegeEditor v-else v-model="draftModel" :groups="groups.data.value!" :users="users.data.value!" :readonly="readonly" :errors="errors" />
    <FieldPermissionEditor
      v-model="draftModel" class="q-mt-lg" :fields="fields" :groups="groups.data.value!" :title-field-id="titleFieldId"
      :readonly="readonly" :errors="errors"
    />
    <div class="row justify-end q-mt-md">
      <q-btn color="primary" :label="t('designer.permissions.save')" :disable="readonly" :loading="save.isPending.value" data-testid="save-permissions" @click="submit" />
    </div>
  </div>
</template>
