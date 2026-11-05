<script setup lang="ts">
import type { QSelect } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import type { DataScope, Group, ModulePermissions, Privileges, User } from 'src/domain/dynamic/types';

const props = defineProps<{
  modelValue: ModulePermissions;
  groups: Group[];
  users: User[];
  readonly: boolean;
  /** Backend 400 errors keyed by JSON path (e.g. `groups[0].scope`); used to mark rows. */
  errors?: Record<string, string[]>;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: ModulePermissions] }>();
const { t, locale } = useI18n();

const AXES = ['read', 'add', 'edit', 'delete', 'printExport', 'pricing', 'approve'] as const;
const emptyPrivileges = (): Privileges => ({ read: false, add: false, edit: false, delete: false, printExport: false, pricing: false, approve: false });

const scopeOptions = computed(() => (['all', 'group', 'own'] as DataScope[]).map((value) => ({
  value, label: t(`designer.permissions.scope${value[0]!.toUpperCase()}${value.slice(1)}`),
})));
const scopeSel = useSelectFilter(scopeOptions);

const groupRow = (id: number) => props.modelValue.groups.find((g) => g.groupId === id);
const groupIndex = (id: number) => props.modelValue.groups.findIndex((g) => g.groupId === id);
function rowErrors(kind: 'groups' | 'users', index: number): string | undefined {
  if (index < 0) return undefined;
  const hit = Object.entries(props.errors ?? {}).filter(([k]) => k.startsWith(`${kind}[${index}]`)).flatMap(([, v]) => v);
  return hit.length ? hit.join('、') : undefined;
}

function toggleGroup(groupId: number, axis: keyof Privileges, value: boolean) {
  const rows = props.modelValue.groups.some((g) => g.groupId === groupId)
    ? props.modelValue.groups
    : [...props.modelValue.groups, { groupId, privileges: emptyPrivileges() }];
  emit('update:modelValue', {
    ...props.modelValue,
    groups: rows.map((g) => (g.groupId === groupId ? { ...g, privileges: { ...g.privileges, [axis]: value } } : g)),
  });
}
function setScope(groupId: number, scope: DataScope | null) {
  const rows = props.modelValue.groups.some((g) => g.groupId === groupId)
    ? props.modelValue.groups
    : [...props.modelValue.groups, { groupId, privileges: emptyPrivileges() }];
  emit('update:modelValue', {
    ...props.modelValue,
    groups: rows.map((g) => (g.groupId === groupId ? { ...g, scope: scope ?? null } : g)),
  });
}

const userName = (id: string) => props.users.find((u) => u.userId === id)?.displayName ?? id;
const addable = computed(() => props.users
  .filter((u) => !props.modelValue.users.some((r) => r.userId === u.userId))
  .map((u) => ({ value: u.userId, label: `${u.displayName} (${u.userId})` })));
const addSel = useSelectFilter(addable);
const addSelEl = ref<QSelect | null>(null);
function addUser(userId: string | null) {
  if (!userId) return;
  addSelEl.value?.updateInputValue('');
  emit('update:modelValue', { ...props.modelValue, users: [...props.modelValue.users, { userId, privileges: emptyPrivileges() }] });
}
function removeUser(userId: string) {
  emit('update:modelValue', { ...props.modelValue, users: props.modelValue.users.filter((u) => u.userId !== userId) });
}
function toggleUser(userId: string, axis: keyof Privileges, value: boolean) {
  emit('update:modelValue', {
    ...props.modelValue,
    users: props.modelValue.users.map((u) => (u.userId === userId ? { ...u, privileges: { ...u.privileges, [axis]: value } } : u)),
  });
}
</script>

<template>
  <div class="q-gutter-y-lg">
    <section>
      <div class="text-subtitle1 q-mb-sm">{{ t('designer.permissions.groups') }}</div>
      <q-markup-table flat bordered dense>
        <thead>
          <tr>
            <th class="text-left">{{ t('designer.permissions.groups') }}</th>
            <th v-for="a in AXES" :key="a">{{ t(`designer.permissions.axes.${a}`) }}</th>
            <th>{{ t('designer.permissions.scope') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="g in groups" :key="g.id" :class="{ 'bg-red-1': rowErrors('groups', groupIndex(g.id)) }" :data-testid="`group-row-${g.id}`">
            <td class="text-left">
              {{ resolveI18n(g.name, locale) }}
              <div v-if="rowErrors('groups', groupIndex(g.id))" class="text-negative text-caption" :data-testid="`group-error-${g.id}`">
                {{ rowErrors('groups', groupIndex(g.id)) }}
              </div>
            </td>
            <td v-for="a in AXES" :key="a" class="text-center">
              <q-checkbox
                :model-value="groupRow(g.id)?.privileges[a] ?? false" :disable="readonly" dense
                :data-testid="`g-${g.id}-${a}`" @update:model-value="toggleGroup(g.id, a, $event)"
              />
            </td>
            <td style="min-width: 140px">
              <q-select :model-value="groupRow(g.id)?.scope ?? null" :options="scopeSel.filtered.value" :disable="readonly" emit-value map-options
                clearable dense outlined :placeholder="t('designer.permissions.scopeAll')" :data-testid="`scope-${g.id}`"
                @update:model-value="setScope(g.id, $event)" use-input input-debounce="0" fill-input hide-selected @filter="scopeSel.onFilter" @popup-hide="scopeSel.reset">
                <template #no-option><SelectNoOption /></template>
              </q-select>
            </td>
          </tr>
        </tbody>
      </q-markup-table>
    </section>

    <section>
      <div class="row items-center q-mb-sm q-gutter-md">
        <div class="text-subtitle1">{{ t('designer.permissions.users') }}</div>
        <q-select :model-value="null" :options="addSel.filtered.value" :label="t('designer.permissions.addUser')" :disable="readonly" emit-value map-options
          dense outlined style="min-width: 220px" data-testid="user-add" @update:model-value="addUser" use-input input-debounce="0" @filter="addSel.onFilter" @popup-hide="addSel.reset" ref="addSelEl">
          <template #no-option><SelectNoOption /></template>
        </q-select>
      </div>
      <q-markup-table flat bordered dense>
        <thead>
          <tr>
            <th class="text-left">{{ t('designer.permissions.users') }}</th>
            <th v-for="a in AXES" :key="a">{{ t(`designer.permissions.axes.${a}`) }}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="(u, i) in modelValue.users" :key="u.userId" :class="{ 'bg-red-1': rowErrors('users', i) }" :data-testid="`user-row-${u.userId}`">
            <td class="text-left">
              {{ userName(u.userId) }}
              <div v-if="rowErrors('users', i)" class="text-negative text-caption" :data-testid="`user-error-${u.userId}`">{{ rowErrors('users', i) }}</div>
            </td>
            <td v-for="a in AXES" :key="a" class="text-center">
              <q-checkbox
                :model-value="u.privileges[a]" :disable="readonly" dense
                :data-testid="`u-${u.userId}-${a}`" @update:model-value="toggleUser(u.userId, a, $event)"
              />
            </td>
            <td>
              <q-btn
                flat dense round icon="close" :title="t('designer.permissions.remove')" :disable="readonly"
                :data-testid="`user-remove-${u.userId}`" @click="removeUser(u.userId)"
              />
            </td>
          </tr>
        </tbody>
      </q-markup-table>
    </section>
  </div>
</template>
