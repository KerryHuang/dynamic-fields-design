<template>
  <q-layout view="hHh Lpr lFf">
    <q-header elevated>
      <q-toolbar>
        <q-toolbar-title>Dynamic Modules POC</q-toolbar-title>
        <span class="q-mr-sm" data-testid="current-user">{{ me?.displayName }}</span>
        <q-btn flat dense no-caps class="q-mr-sm" :label="t('auth.changePassword')" to="/change-password" data-testid="change-password-link" />
        <q-btn flat dense no-caps :label="t('auth.logout')" data-testid="logout" @click="logout" />
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

        <template v-if="me?.designer.read || me?.isSystemAdmin">
          <q-separator class="q-my-sm" />
          <q-item-label header>{{ t('menu.system') }}</q-item-label>
          <q-item v-if="me.isSystemAdmin" clickable to="/system/users" data-testid="users-menu">
            <q-item-section>{{ t('menu.users') }}</q-item-section>
          </q-item>
          <q-item v-if="me.isSystemAdmin" clickable to="/system/groups" data-testid="groups-menu">
            <q-item-section>{{ t('menu.groups') }}</q-item-section>
          </q-item>
          <template v-if="me.designer.read">
            <q-item clickable to="/system/dynamic-modules" data-testid="designer-menu">
              <q-item-section>{{ t('menu.dynamicModules') }}</q-item-section>
            </q-item>
            <q-item clickable to="/system/demo-data" data-testid="demo-data-menu">
              <q-item-section>{{ t('menu.demoData') }}</q-item-section>
            </q-item>
          </template>
        </template>
      </q-list>
    </q-drawer>

    <q-page-container>
      <!-- keyed by user: a new session remounts the page, so it refetches and re-initialises -->
      <router-view :key="identity.userId" />
    </q-page-container>
  </q-layout>
</template>

<script setup lang="ts">
import { computed, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useLogout } from 'src/application/dynamic/queries/auth-queries';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const identity = useIdentityStore();
const router = useRouter();
const logoutMutation = useLogout();

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
watch(users, (list) => { if (list) identity.setUsers(list); }, { immediate: true });

function logout() {
  logoutMutation.mutate(undefined, { onSettled: () => { void router.replace('/login'); } });
}
</script>
