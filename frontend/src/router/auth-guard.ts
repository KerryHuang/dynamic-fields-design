import type { QueryClient } from '@tanstack/vue-query';
import type { Router } from 'vue-router';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import { loadSessionSettings, resetSessionSettings, sessionSettingsLoaded } from 'src/application/dynamic/session-settings';
import { i18n } from 'src/i18n/instance';
import { previewLocale } from 'src/i18n/locale-preview';
import { installedLocales } from 'src/i18n/locale-settings';
import { applyStartupOptions, startupOutcome, takeStartupAuthOptions } from 'src/i18n/startup';
import { endPreview, switchLocale } from 'src/i18n/ui-messages';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';

/** Only same-origin absolute paths: starts with a single '/', never '//' or '/\' (both are protocol-relative to a browser). */
export function safeRedirect(path: string | undefined): string {
  return path && path.startsWith('/') && path[1] !== '/' && path[1] !== '\\' ? path : '/';
}

const loginLocation = (redirect: string) => ({ path: '/login', query: redirect === '/' ? {} : { redirect } });

const SERVICE_UNAVAILABLE = '/service-unavailable';

/**
 * 登入前就要知道語系資訊與預設、啟用語系：消費啟動那一次 auth/options（最晚在啟動預算到時 resolve，不另打 API）。
 * 決定語系與載入介面文字在 src/i18n/startup.ts，這裡不套語系、不等文字。
 */
async function loadLocaleSettings(): Promise<void> {
  applyStartupOptions((await startupOutcome()).options);
}

/**
 * 重新整理後的試看（i4b 計畫判斷 17）：啟動時不套試看值；登入後的生效值（完整已安裝清單）載入後，只有這個分頁 /me 確認過的
 * 系統管理員、且試看值仍已安裝，才在背景切過去（不阻塞導覽）；其餘清除。
 */
function resumePreview(admin: { userId: string; isSystemAdmin: boolean } | null, userId: string): void {
  const p = previewLocale();
  if (!p) return;
  if (admin?.isSystemAdmin && admin.userId === userId && installedLocales().includes(p)) void switchLocale(i18n, p, { preview: true });
  else endPreview(i18n);
}

/**
 * First navigation loads the session from GET /me. Only a 401 means signed out; any other failure (500, network) says nothing
 * about the cookie, so keep the store, probe again next time and show the retry page.
 */
export function installAuthGuard(router: Router) {
  let sessionLoaded = false;
  let admin: { userId: string; isSystemAdmin: boolean } | null = null;
  router.beforeEach(async (to) => {
    if (to.path === SERVICE_UNAVAILABLE) return true;
    const identity = useIdentityStore();
    if (!sessionLoaded) {
      await loadLocaleSettings();
      try {
        const me = await dynamicApi.getMe();
        identity.setSession(me.userId, me.mustChangePassword);
        identity.setDemoAccount(me.isDemoAccount === true);
        admin = { userId: me.userId, isSystemAdmin: me.isSystemAdmin === true };
        // 版面載入前就知道能不能存，Demo 訪客不會在空窗內打到儲存 API
        if (me.theme) useThemeStore().applyMe(me.theme);
        // 已登入就用不到登入頁那份啟動結果，丟掉，免得之後登出時登入頁拿到舊資料
        takeStartupAuthOptions();
        sessionLoaded = true;
      } catch (e) {
        if ((e as ApiError | null)?.status !== 401) {
          return { path: SERVICE_UNAVAILABLE, query: to.fullPath === '/' ? {} : { redirect: to.fullPath } };
        }
        identity.clear();
        endPreview(i18n);
        sessionLoaded = true;
      }
    }
    if (!identity.userId) return to.meta.public ? true : loginLocation(to.fullPath);
    // 強制改密碼優先（Demo 帳號被管理員重設密碼後會同時成立）；只在非強制時擋 Demo 的改密碼入口。
    // 要在載入營運時區之前：強制改密碼期間後端只放行 /auth/* 與 GET /me，政策 API 會回 403
    if (identity.mustChangePassword) {
      endPreview(i18n);
      return to.path === '/change-password' ? true : '/change-password';
    }
    if (identity.isDemoAccount && to.path === '/change-password') return '/';
    // 登入後的頁面才用得到營運時區：每次登入後的第一次導覽載入一次（公開頁面與強制改密碼頁不載）
    if (!to.meta.public && !sessionSettingsLoaded()) {
      try {
        await loadSessionSettings();
      } catch {
        // 試看值保留，下次成功載入時再驗
        return { path: SERVICE_UNAVAILABLE, query: to.fullPath === '/' ? {} : { redirect: to.fullPath } };
      }
      resumePreview(admin, identity.userId);
    }
    return to.meta.public ? '/' : true;
  });
}

/** A 401 from a normal request: the session expired. Return to the current page after logging in again. */
export function handleUnauthorized(router: Router, queryClient: QueryClient) {
  const identity = useIdentityStore();
  // Already signed out: parallel 401s, or the /me probe of the first navigation, which the guard handles itself.
  if (!identity.userId) return;
  const redirect = router.currentRoute.value.fullPath;
  identity.clear();
  queryClient.clear();
  resetSessionSettings();
  endPreview(i18n);
  void router.replace(loginLocation(redirect));
}

/** 403 passwordChangeRequired: another tab or an admin reset flagged the account. */
export function handlePasswordChangeRequired(router: Router) {
  useIdentityStore().mustChangePassword = true;
  if (router.currentRoute.value.path !== '/change-password') void router.replace('/change-password');
}
