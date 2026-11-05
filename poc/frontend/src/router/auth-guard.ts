import type { QueryClient } from '@tanstack/vue-query';
import type { Router } from 'vue-router';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

/** Only same-origin absolute paths: starts with a single '/', never '//' or '/\' (both are protocol-relative to a browser). */
export function safeRedirect(path: string | undefined): string {
  return path && path.startsWith('/') && path[1] !== '/' && path[1] !== '\\' ? path : '/';
}

const loginLocation = (redirect: string) => ({ path: '/login', query: redirect === '/' ? {} : { redirect } });

const SERVICE_UNAVAILABLE = '/service-unavailable';

/**
 * First navigation loads the session from GET /me. Only a 401 means signed out; any other failure (500, network) says nothing
 * about the cookie, so keep the store, probe again next time and show the retry page.
 */
export function installAuthGuard(router: Router) {
  let sessionLoaded = false;
  router.beforeEach(async (to) => {
    if (to.path === SERVICE_UNAVAILABLE) return true;
    const identity = useIdentityStore();
    if (!sessionLoaded) {
      try {
        const me = await dynamicApi.getMe();
        identity.setSession(me.userId, me.mustChangePassword);
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
