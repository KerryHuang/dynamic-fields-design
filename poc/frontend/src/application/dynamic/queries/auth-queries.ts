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
  return (u: AuthUser) => {
    qc.clear();
    identity.setSession(u.userId, u.mustChangePassword);
  };
}

export function useLogin() {
  const start = useStartSession();
  return useMutation({ mutationFn: (body: { userId: string; password: string }) => dynamicApi.login(body), onSuccess: start });
}

export function useDemoLogin() {
  const start = useStartSession();
  return useMutation({ mutationFn: (userId: string) => dynamicApi.demoLogin(userId), onSuccess: start });
}

/** Signs out locally even when the API call fails: the cookie then expires on its own. */
export function useLogout() {
  const qc = useQueryClient();
  const identity = useIdentityStore();
  return useMutation({
    mutationFn: () => dynamicApi.logout(),
    onSettled: () => {
      qc.clear();
      identity.clear();
    },
  });
}

export function useChangePassword() {
  const identity = useIdentityStore();
  return useMutation({
    mutationFn: (body: { oldPassword: string; newPassword: string }) => dynamicApi.changePassword(body),
    onSuccess: (u) => identity.setSession(u.userId, u.mustChangePassword),
  });
}
