import type { QueryClient } from '@tanstack/vue-query';
import type { Router } from 'vue-router';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';

/** Only same-origin absolute paths: starts with a single '/', never '//' or '/\' (both are protocol-relative to a browser). */
export function safeRedirect(path: string | undefined): string {
  return path && path.startsWith('/') && path[1] !== '/' && path[1] !== '\\' ? path : '/';
}

const loginLocation = (redirect: string) => ({ path: '/login', query: redirect === '/' ? {} : { redirect } });

/** First navigation loads the session from GET /me (a 401 means signed out); afterwards the store is the source of truth. */
export function installAuthGuard(router: Router) {
  let sessionLoaded = false;
  router.beforeEach(async (to) => {
    const identity = useIdentityStore();
    if (!sessionLoaded) {
      sessionLoaded = true;
      try {
        const me = await dynamicApi.getMe();
        identity.setSession(me.userId, me.mustChangePassword);
      } catch {
        identity.clear();
      }
    }
    if (!identity.userId) return to.meta.public ? true : loginLocation(to.fullPath);
    if (identity.mustChangePassword) return to.path === '/change-password' ? true : '/change-password';
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
  void router.replace(loginLocation(redirect));
}

/** 403 passwordChangeRequired: another tab or an admin reset flagged the account. */
export function handlePasswordChangeRequired(router: Router) {
  useIdentityStore().mustChangePassword = true;
  if (router.currentRoute.value.path !== '/change-password') void router.replace('/change-password');
}
