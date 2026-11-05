import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { AdminUserCreate, AdminUserUpdate } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useAdminUsers() {
  return useQuery({ queryKey: ['admin-users'], queryFn: dynamicApi.getAdminUsers });
}

/** useUsers is staleTime Infinity, so ['users'] must be invalidated or new accounts never show; ['me'] covers an admin editing their own flag. */
function useAdminMutation<V>(fn: (v: V) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => Promise.all(['admin-users', 'users', 'me'].map((k) => qc.invalidateQueries({ queryKey: [k] }))),
  });
}

export const useCreateUser = () => useAdminMutation((body: AdminUserCreate) => dynamicApi.createUser(body));
export const useUpdateUser = () => useAdminMutation((v: { id: string; body: AdminUserUpdate }) => dynamicApi.updateUser(v.id, v.body));
export const useResetPassword = () => useAdminMutation((v: { id: string; password: string }) => dynamicApi.resetUserPassword(v.id, v.password));
export const useSetUserActive = () => useAdminMutation((v: { id: string; active: boolean }) => dynamicApi.setUserActive(v.id, v.active));
