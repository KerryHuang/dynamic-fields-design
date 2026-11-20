import type { QueryClient } from '@tanstack/vue-query';
import type { Router } from 'vue-router';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import { loadSessionSettings, resetSessionSettings, sessionSettingsLoaded } from 'src/application/dynamic/session-settings';
import { BUILTIN_LOCALES, FALLBACK_LOCALE, applyDocumentLocale, applyLocale, readLocale } from 'src/i18n/locale';
import { setLocaleInfos, setLocaleSettings, toLocaleCodes, type LocaleInfo } from 'src/i18n/locale-settings';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';

/** Only same-origin absolute paths: starts with a single '/', never '//' or '/\' (both are protocol-relative to a browser). */
export function safeRedirect(path: string | undefined): string {
  return path && path.startsWith('/') && path[1] !== '/' && path[1] !== '\\' ? path : '/';
}

const loginLocation = (redirect: string) => ({ path: '/login', query: redirect === '/' ? {} : { redirect } });

const SERVICE_UNAVAILABLE = '/service-unavailable';

/**
 * 登入前就要知道語系資訊與預設、啟用語系（GET auth/options）：瀏覽器沒記過語系、或記的語系已停用時套用預設（不寫入 localStorage）。
 * 取不到時（伺服器連不上）維持繁中、內建四語可選（語系包 i4a 計畫判斷 22），之後登入後的全系統生效值會再更新。
 * 拿到語系資訊後一律對目前語系重套 Quasar 語言包：boot 時還沒有資訊，記住的語系先載了代碼同名的包（判斷 17）。
 */
async function loadLocaleSettings(locale: { value: string } | undefined): Promise<void> {
  let infos: LocaleInfo[] = BUILTIN_LOCALES;
  let def = FALLBACK_LOCALE;
  let enabled = BUILTIN_LOCALES.map((l) => l.code);
  try {
    const o = await dynamicApi.getAuthOptions();
    setLocaleInfos(o.locales);
    const list = toLocaleCodes(o.enabledLocales);
    const d = toLocaleCodes([o.defaultLocale])[0];
    if (d && list.length) { infos = o.locales; def = d; enabled = list; }
  } catch {
    /* 維持繁中 */
  }
  setLocaleInfos(infos);
  setLocaleSettings({ defaultLocale: def, enabled });
  if (!locale) return;
  const stored = readLocale();
  if (!stored || !enabled.includes(stored)) applyLocale(def, locale, false);
  else applyDocumentLocale(locale.value);
}

/**
 * First navigation loads the session from GET /me. Only a 401 means signed out; any other failure (500, network) says nothing
 * about the cookie, so keep the store, probe again next time and show the retry page.
 */
export function installAuthGuard(router: Router, locale?: { value: string }) {
  let sessionLoaded = false;
  router.beforeEach(async (to) => {
    if (to.path === SERVICE_UNAVAILABLE) return true;
    const identity = useIdentityStore();
    if (!sessionLoaded) {
      await loadLocaleSettings(locale);
      try {
        const me = await dynamicApi.getMe();
        identity.setSession(me.userId, me.mustChangePassword);
        identity.setDemoAccount(me.isDemoAccount === true);
        // 版面載入前就知道能不能存，Demo 訪客不會在空窗內打到儲存 API
        if (me.theme) useThemeStore().applyMe(me.theme);
        sessionLoaded = true;
      } catch (e) {
        if ((e as ApiError | null)?.status !== 401) {
          return { path: SERVICE_UNAVAILABLE, query: to.fullPath === '/' ? {} : { redirect: to.fullPath } };
        }
        identity.clear();
        sessionLoaded = true;
      }
    }
    if (!identity.userId) return to.meta.public ? true : loginLocation(to.fullPath);
    // 強制改密碼優先（Demo 帳號被管理員重設密碼後會同時成立）；只在非強制時擋 Demo 的改密碼入口。
    // 要在載入營運時區之前：強制改密碼期間後端只放行 /auth/* 與 GET /me，政策 API 會回 403
    if (identity.mustChangePassword) return to.path === '/change-password' ? true : '/change-password';
    if (identity.isDemoAccount && to.path === '/change-password') return '/';
    // 登入後的頁面才用得到營運時區：每次登入後的第一次導覽載入一次（公開頁面與強制改密碼頁不載）
    if (!to.meta.public && !sessionSettingsLoaded()) {
      try {
        await loadSessionSettings();
      } catch {
        return { path: SERVICE_UNAVAILABLE, query: to.fullPath === '/' ? {} : { redirect: to.fullPath } };
      }
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
  void router.replace(loginLocation(redirect));
}

/** 403 passwordChangeRequired: another tab or an admin reset flagged the account. */
export function handlePasswordChangeRequired(router: Router) {
  useIdentityStore().mustChangePassword = true;
  if (router.currentRoute.value.path !== '/change-password') void router.replace('/change-password');
}
