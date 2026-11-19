import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { AuthUser } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { SECRET_MUTATION, useSecretMutation } from './secret-mutation';

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

/** 密碼用 send 綁定當次提交，不進 mutation variables（見 useSecretMutation）。 */
export function useLogin() {
  const start = useStartSession();
  return useSecretMutation((take: (b: { userId: string }) => { password: string }) =>
    useMutation({ ...SECRET_MUTATION, mutationFn: (b: { userId: string }) => dynamicApi.login({ ...b, ...take(b) }), onSuccess: start }));
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

/** 同 useLogin：新舊密碼用 send 綁定當次提交（variables 傳新建的 {}）。 */
export function useChangePassword() {
  const identity = useIdentityStore();
  return useSecretMutation((take: (v: object) => { oldPassword: string; newPassword: string }) =>
    useMutation({
      ...SECRET_MUTATION,
      mutationFn: (v: object) => dynamicApi.changePassword(take(v)),
      onSuccess: (u) => identity.setSession(u.userId, u.mustChangePassword),
    }));
}
