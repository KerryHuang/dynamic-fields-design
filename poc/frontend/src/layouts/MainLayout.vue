<template>
  <q-layout view="hHh Lpr lFf">
    <q-header elevated>
      <q-toolbar>
        <q-toolbar-title><router-link to="/" class="brand-link" data-testid="brand">Formwright</router-link></q-toolbar-title>
        <span class="q-mr-sm" data-testid="current-user">{{ me?.displayName }}</span>
        <q-btn flat dense no-caps class="q-mr-sm" icon="palette" :label="t('theme.title')" data-testid="theme-open" @click="themeOpen = true" />
        <q-btn v-if="!identity.isDemoAccount" flat dense no-caps class="q-mr-sm" :label="t('auth.changePassword')" to="/change-password" data-testid="change-password-link" />
        <span v-if="logoutError" class="q-mr-sm bg-negative text-white q-px-sm rounded-borders" data-testid="logout-error">{{ t('auth.logoutFailed') }}</span>
        <q-btn flat dense no-caps :label="t('auth.logout')" :loading="logoutMutation.isPending.value" data-testid="logout" @click="logout" />
      </q-toolbar>
      <AppBreadcrumb v-if="theme.effective.style === 'enterprise'" :menu="menu ?? []" />
    </q-header>

    <q-drawer :model-value="true" show-if-above bordered :width="theme.sidebarWidth">
      <q-list>
        <q-item clickable exact to="/" data-testid="home-menu">
          <q-item-section avatar><q-icon name="home" /></q-item-section>
          <q-item-section>{{ t('menu.home') }}</q-item-section>
        </q-item>

        <template v-for="g in menuGroups" :key="g.closed ? '__closed' : (g.title ?? '')">
          <q-item-label header :data-testid="g.closed ? 'closed-apps-group' : undefined">{{ g.closed ? t('menu.closedApps') : (g.title ?? t('menu.custom')) }}</q-item-label>
          <q-item
            v-for="m in g.items"
            :key="m.key"
            clickable
            :to="`/dynamic/${m.key}`"
          >
            <q-item-section avatar><q-icon :name="m.icon || 'apps'" /></q-item-section>
            <q-item-section>{{ resolveI18n(m.name, locale) }}</q-item-section>
          </q-item>
        </template>

        <q-separator class="q-my-sm" />
        <q-item-label header>{{ t('menu.reports') }}</q-item-label>
        <q-item clickable to="/dashboards" data-testid="dashboards-menu">
          <q-item-section avatar><q-icon name="dashboard" /></q-item-section>
          <q-item-section>{{ t('menu.dashboards') }}</q-item-section>
        </q-item>
        <q-item clickable to="/reports" data-testid="reports-menu">
          <q-item-section avatar><q-icon name="bar_chart" /></q-item-section>
          <q-item-section>{{ t('menu.myReports') }}</q-item-section>
        </q-item>

        <template v-if="links.length">
          <q-separator class="q-my-sm" />
          <q-item-label header>{{ t('menu.system') }}</q-item-label>
          <q-item v-for="l in links" :key="l.to" clickable :to="l.to" :data-testid="l.testid">
            <q-item-section avatar><q-icon :name="l.icon" /></q-item-section>
            <q-item-section>{{ t(l.label) }}</q-item-section>
          </q-item>
        </template>
      </q-list>
    </q-drawer>

    <q-page-container>
      <!-- keyed by user: a new session remounts the page, so it refetches and re-initialises -->
      <router-view :key="identity.userId" />
    </q-page-container>

    <ThemeDialog v-model="themeOpen" />
  </q-layout>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useLogout } from 'src/application/dynamic/queries/auth-queries';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { recordRecent } from 'src/application/dynamic/recent-modules';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { groupMenu, systemLinks } from 'src/domain/dynamic/menu-nav';
import AppBreadcrumb from 'src/presentation/theme/AppBreadcrumb.vue';
import ThemeDialog from 'src/presentation/theme/ThemeDialog.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';

const { t, locale } = useI18n();
const identity = useIdentityStore();
const theme = useThemeStore();
const themeOpen = ref(false);
const router = useRouter();
const logoutMutation = useLogout();

const { data: menu } = useMenu();
const menuGroups = computed(() => groupMenu(menu.value ?? []));
const { data: me } = useMe();
const links = computed(() => systemLinks(me.value));
// 首頁的「最近開過」：每次進到某個模組就記一筆
const route = useRoute();
watch(
  () => [identity.userId, route.params.moduleKey] as const,
  ([userId, key]) => { if (typeof key === 'string') recordRecent(userId, key); },
  { immediate: true },
);
const { data: users } = useUsers();
watch(me, (m) => { if (m) identity.setDemoAccount(m.isDemoAccount === true); }, { immediate: true });
watch(users, (list) => { if (list) identity.setUsers(list); }, { immediate: true });

const logoutError = ref(false);
function logout() {
  logoutError.value = false;
  logoutMutation.mutate(undefined, {
    onSuccess: () => { void router.replace('/login'); },
    onError: () => { logoutError.value = true; },
  });
}
</script>

<style scoped>
.brand-link {
  color: inherit;
  text-decoration: none;
}
</style>
