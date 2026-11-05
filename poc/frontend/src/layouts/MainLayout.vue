<template>
  <q-layout view="hHh Lpr lFf">
    <q-header elevated>
      <q-toolbar>
        <q-toolbar-title>Dynamic Modules POC</q-toolbar-title>
        <span class="q-mr-sm">{{ t('identity.user') }}</span>
        <q-select :model-value="identity.userId"
          :options="userSel.filtered.value"
          use-input
          input-debounce="0"
          fill-input
          hide-selected
          @filter="userSel.onFilter"
          @popup-hide="userSel.reset"
          option-value="userId"
          option-label="displayName"
          emit-value
          map-options
          dense
          dark
          borderless
          options-dense
          data-testid="identity-select"
          @update:model-value="switchUser">
          <template #no-option><SelectNoOption /></template>
        </q-select>
      </q-toolbar>
    </q-header>

    <q-drawer :model-value="true" show-if-above bordered :width="240">
      <q-list>
        <template v-for="g in menuGroups" :key="g.title ?? ''">
          <q-item-label header>{{ g.title ?? t('menu.custom') }}</q-item-label>
          <q-item
            v-for="m in g.items"
            :key="m.key"
            clickable
            :to="`/dynamic/${m.key}`"
          >
            <q-item-section v-if="m.icon" avatar><q-icon :name="m.icon" /></q-item-section>
            <q-item-section>{{ resolveI18n(m.name, locale) }}</q-item-section>
          </q-item>
        </template>

        <template v-if="me?.designer.read">
          <q-separator class="q-my-sm" />
          <q-item-label header>{{ t('menu.system') }}</q-item-label>
          <q-item clickable to="/system/dynamic-modules" data-testid="designer-menu">
            <q-item-section>{{ t('menu.dynamicModules') }}</q-item-section>
          </q-item>
        </template>
      </q-list>
    </q-drawer>

    <q-page-container>
      <!-- keyed by user: switching identity remounts the page, so it refetches and re-initialises -->
      <router-view :key="identity.userId" />
    </q-page-container>
  </q-layout>
</template>

<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { computed, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import { useSelectFilter } from 'src/components/framework/useSelectFilter';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const identity = useIdentityStore();
const userSel = useSelectFilter(() => identity.users, (u) => u.displayName);
const queryClient = useQueryClient();

const { data: menu } = useMenu();
// Unsectioned items form the leading 自訂功能 group (title undefined); sections follow by name; /menu order holds inside a group.
const menuGroups = computed(() => {
  const groups = new Map<string | undefined, NonNullable<typeof menu.value>>();
  for (const m of menu.value ?? []) {
    const g = groups.get(m.section || undefined);
    if (g) g.push(m); else groups.set(m.section || undefined, [m]);
  }
  return [...groups.entries()]
    .map(([title, items]) => ({ title, items }))
    .sort((a, b) => (a.title === undefined ? -1 : b.title === undefined ? 1 : a.title.localeCompare(b.title)));
});
const { data: me } = useMe();
const { data: users } = useUsers();
// A fallback identity change must reset the cache just like switchUser does.
watch(users, (list) => { if (list && identity.syncUsers(list)) queryClient.clear(); }, { immediate: true });

function switchUser(id: string) {
  identity.setUser(id);
  queryClient.clear();
}
</script>
