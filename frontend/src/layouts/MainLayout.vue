<template>
  <q-layout view="hHh Lpr lFf">
    <q-header elevated>
      <q-toolbar>
        <!-- 手機與平板（Quasar md 斷點以下）選單是覆蓋式抽屜，要有開關；筆電以上常駐，不需要 -->
        <q-btn
          v-if="$q.screen.lt.md || collapseDrawer" flat dense round icon="menu" class="q-mr-xs" :aria-label="t('menu.toggle')"
          :aria-expanded="drawerOpen" data-testid="menu-toggle" @click="drawerOpen = !drawerOpen"
        />
        <q-toolbar-title><router-link to="/" class="brand-link" data-testid="brand">Formwright</router-link></q-toolbar-title>
        <span class="q-mr-sm gt-xs" data-testid="current-user">{{ me?.displayName }}</span>
        <q-btn
          flat dense no-caps class="q-mr-sm" icon="palette" :label="$q.screen.gt.xs ? t('theme.title') : undefined" :aria-label="t('theme.title')"
          data-testid="theme-open" @click="themeOpen = true"
        />
        <NotificationBell />
        <LocaleSwitcher class="q-mr-sm" :allow-preview="me?.isSystemAdmin === true" />
        <q-btn
          v-if="!identity.isDemoAccount" flat dense no-caps class="q-mr-sm" icon="key" :label="$q.screen.lt.sm ? undefined : t('auth.changePassword')"
          :aria-label="t('auth.changePassword')" to="/change-password" data-testid="change-password-link"
        >
          <q-tooltip v-if="$q.screen.lt.sm">{{ t('auth.changePassword') }}</q-tooltip>
        </q-btn>
        <q-btn
          v-if="showAiEntry" flat dense no-caps class="q-mr-sm" icon="smart_toy" :label="$q.screen.lt.sm ? undefined : t('aiTokens.entry')"
          :aria-label="t('aiTokens.entry')" to="/account/ai-tokens" data-testid="ai-tokens-link"
        >
          <q-tooltip v-if="$q.screen.lt.sm">{{ t('aiTokens.entry') }}</q-tooltip>
        </q-btn>
        <span v-if="logoutError" class="q-mr-sm bg-negative text-white q-px-sm rounded-borders" data-testid="logout-error">{{ t('auth.logoutFailed') }}</span>
        <q-btn flat dense no-caps :label="t('auth.logout')" :loading="logoutMutation.isPending.value" data-testid="logout" @click="logout" />
      </q-toolbar>
      <AppBreadcrumb v-if="theme.effective.style === 'enterprise'" :menu="menu ?? []" />
    </q-header>

    <q-drawer v-model="drawerOpen" :show-if-above="!collapseDrawer" :no-swipe-open="collapseDrawer" bordered :width="theme.sidebarWidth" :breakpoint="1024">
      <q-list @click="closeWhenOverlay">
        <q-item clickable exact to="/" data-testid="home-menu">
          <q-item-section avatar><q-icon name="home" /></q-item-section>
          <q-item-section>{{ t('menu.home') }}</q-item-section>
        </q-item>
        <q-item clickable to="/tasks" data-testid="my-tasks-menu">
          <q-item-section avatar><q-icon name="task_alt" /></q-item-section>
          <q-item-section>{{ t('menu.myTasks') }}</q-item-section>
        </q-item>
        <q-item clickable to="/approvals" data-testid="approvals-menu">
          <q-item-section avatar><q-icon name="fact_check" /></q-item-section>
          <q-item-section>{{ t('menu.approvals') }}</q-item-section>
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
import { useMyMcp, useMyMcpTokens } from 'src/application/dynamic/queries/mcp-queries';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import { useBusinessTimeWatch } from 'src/application/dynamic/queries/use-business-time-watch';
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
// 路由 meta `collapseDrawer`（流程圖設計頁）：選單預設收起、可再打開，滑動不會拉出選單（平板的畫布手勢不被搶）；換到沒有這個 meta 的路由恢復原行為。
const collapseDrawer = computed(() => router.currentRoute.value.meta.collapseDrawer === true);
watch(collapseDrawer, (c) => { drawerOpen.value = c ? false : !overlayMode.value; });
const logoutMutation = useLogout();
useBusinessTimeWatch();

const { data: menu } = useMenu();
const menuGroups = computed(() => groupMenu(menu.value ?? [], locale.value));
const { data: me } = useMe();
const links = computed(() => systemLinks(me.value));
// 「AI 存取權杖」入口（MCP 設計 §13.5）：能用就顯示；不能用的原因是管理員關閉、不在可用對象、政策有誤時，只有已經有權杖的人才顯示
// （要能進去看與撤銷）；Demo 站（demoSite、accountBlocked）固定顯示，頁面會說明為什麼不開放。
const mcp = useMyMcp();
const mcpReason = computed(() => mcp.data.value?.reason ?? null);
const needsTokenCheck = computed(() => mcp.data.value?.canUse === false && ['disabled', 'notAllowed', 'policyInvalid'].includes(mcpReason.value ?? ''));
const { data: ownTokens } = useMyMcpTokens(needsTokenCheck);
const showAiEntry = computed(() => mcp.data.value?.canUse === true || ['demoSite', 'accountBlocked'].includes(mcpReason.value ?? '')
  || (needsTokenCheck.value && (ownTokens.value?.length ?? 0) > 0));
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
