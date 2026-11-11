<template>
  <q-layout view="hHh Lpr lFf">
    <q-header elevated>
      <q-toolbar>
        <!-- 手機與平板（Quasar md 斷點以下）選單是覆蓋式抽屜，要有開關；筆電以上常駐，不需要 -->
        <q-btn
          v-if="$q.screen.lt.md" flat dense round icon="menu" class="q-mr-xs" :aria-label="t('menu.toggle')"
          :aria-expanded="drawerOpen" data-testid="menu-toggle" @click="drawerOpen = !drawerOpen"
        />
        <q-toolbar-title><router-link to="/" class="brand-link" data-testid="brand">Formwright</router-link></q-toolbar-title>
        <span class="q-mr-sm gt-xs" data-testid="current-user">{{ me?.displayName }}</span>
        <q-btn
          flat dense no-caps class="q-mr-sm" icon="palette" :label="$q.screen.gt.xs ? t('theme.title') : undefined" :aria-label="t('theme.title')"
          data-testid="theme-open" @click="themeOpen = true"
        />
        <NotificationBell />
        <LocaleSwitcher class="q-mr-sm" />
        <q-btn
          v-if="!identity.isDemoAccount" flat dense no-caps class="q-mr-sm" icon="key" :label="$q.screen.lt.sm ? undefined : t('auth.changePassword')"
          :aria-label="t('auth.changePassword')" to="/change-password" data-testid="change-password-link"
        >
          <q-tooltip v-if="$q.screen.lt.sm">{{ t('auth.changePassword') }}</q-tooltip>
        </q-btn>
        <span v-if="logoutError" class="q-mr-sm bg-negative text-white q-px-sm rounded-borders" data-testid="logout-error">{{ t('auth.logoutFailed') }}</span>
        <q-btn flat dense no-caps :label="t('auth.logout')" :loading="logoutMutation.isPending.value" data-testid="logout" @click="logout" />
      </q-toolbar>
      <AppBreadcrumb v-if="theme.effective.style === 'enterprise'" :menu="menu ?? []" />
    </q-header>

    <q-drawer v-model="drawerOpen" show-if-above bordered :width="theme.sidebarWidth" :breakpoint="1024">
      <q-list @click="closeWhenOverlay">
        <q-item clickable exact to="/" data-testid="home-menu">
          <q-item-section avatar><q-icon name="home" /></q-item-section>
          <q-item-section>{{ t('menu.home') }}</q-item-section>
        </q-item>
        <q-item clickable to="/tasks" data-testid="my-tasks-menu">
          <q-item-section avatar><q-icon name="task_alt" /></q-item-section>
          <q-item-section>{{ t('menu.myTasks') }}</q-item-section>
        </q-item>

        <template v-for="g in menuGroups" :key="g.closed ? '__closed' : (g.key ?? '')">
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
import { useQuasar } from 'quasar';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useLogout } from 'src/application/dynamic/queries/auth-queries';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { recordRecent } from 'src/application/dynamic/recent-modules';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { groupMenu, systemLinks } from 'src/domain/dynamic/menu-nav';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import NotificationBell from 'src/presentation/notifications/NotificationBell.vue';
import AppBreadcrumb from 'src/presentation/theme/AppBreadcrumb.vue';
import ThemeDialog from 'src/presentation/theme/ThemeDialog.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';

const { t, locale } = useI18n();
const identity = useIdentityStore();
const theme = useThemeStore();
const themeOpen = ref(false);
const $q = useQuasar();
// 寬螢幕常駐（show-if-above 啟動時展開）；手機與平板是覆蓋式抽屜，預設收合
const drawerOpen = ref(false);
const overlayMode = computed(() => $q.screen.lt.md);
/** 覆蓋式抽屜擋住內容：點選單內任何項目（含目前所在頁，路由不會變）、點遮罩、按 Esc 都關閉。 */
function closeWhenOverlay() { if (overlayMode.value) drawerOpen.value = false; }
function onEscape(e: KeyboardEvent) { if (e.key === 'Escape' && overlayMode.value && drawerOpen.value) drawerOpen.value = false; }
onMounted(() => window.addEventListener('keydown', onEscape));
onBeforeUnmount(() => window.removeEventListener('keydown', onEscape));
const router = useRouter();
const logoutMutation = useLogout();

const { data: menu } = useMenu();
const menuGroups = computed(() => groupMenu(menu.value ?? [], locale.value));
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
