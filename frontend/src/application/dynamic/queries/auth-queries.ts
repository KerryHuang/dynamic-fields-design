import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { AuthUser } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';

/** Polls while the server is generating demo accounts so they appear without a reload. */
export function useAuthOptions() {
  return useQuery({
    queryKey: ['auth-options'],
    queryFn: dynamicApi.getAuthOptions,
    refetchInterval: (q) => (q.state.data?.demoGenerating ? 2000 : false),
  });
}

/** A new session must not see the previous user's cached data. */
function useStartSession() {
  const qc = useQueryClient();
  const identity = useIdentityStore();
  return async (u: AuthUser) => {
    qc.clear();
    identity.setSession(u.userId, u.mustChangePassword);
    // 登入回應沒有 isDemoAccount；導頁前先從 /me 補齊，守衛才擋得住 Demo 帳號進改密碼頁。失敗時留 false，下次載入由守衛補。
    try {
      identity.setDemoAccount((await dynamicApi.getMe()).isDemoAccount === true);
    } catch { /* keep false */ }
  };
}

/** 密碼由頁面在送出當下提供，不進 mutation variables（MutationCache 保留 variables 與 mutationFn 閉包）。 */
export function useLogin(secret: () => { password: string }) {
  const start = useStartSession();
  return useMutation({ mutationFn: (body: { userId: string }) => dynamicApi.login({ ...body, ...secret() }), onSuccess: start });
}

export function useDemoLogin() {
  const start = useStartSession();
  return useMutation({ mutationFn: (userId: string) => dynamicApi.demoLogin(userId), onSuccess: start });
}

/**
 * Clears the local session only after the server confirmed the logout. On a failure (network down) the cookie is still valid,
 * and a reload would return to the same account, so the caller keeps the page and shows an error.
 */
export function useLogout() {
  const qc = useQueryClient();
  const identity = useIdentityStore();
  return useMutation({
    mutationFn: () => dynamicApi.logout(),
    onSuccess: () => {
      qc.clear();
      identity.clear();
    },
  });
}

/** 同 useLogin：新舊密碼送出當下才向頁面取，不進 mutation variables。 */
export function useChangePassword(secret: () => { oldPassword: string; newPassword: string }) {
  const identity = useIdentityStore();
  return useMutation({
    mutationFn: () => dynamicApi.changePassword(secret()),
    onSuccess: (u) => identity.setSession(u.userId, u.mustChangePassword),
  });
}
