import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { AdminUserCreate, AdminUserUpdate } from 'src/domain/dynamic/types';
import { toValue, type MaybeRef } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useAdminUsers() {
  return useQuery({ queryKey: ['admin-users'], queryFn: dynamicApi.getAdminUsers });
}

/** useUsers is staleTime Infinity, so ['users'] must be invalidated or new accounts never show; ['me'] covers an admin editing their own flag; group lists carry member ids, so they change with a user's groups. */
function useAdminMutation<V>(fn: (v: V) => Promise<unknown>, keys = ['admin-users', 'users', 'me', 'admin-groups', 'groups']) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => Promise.all(keys.map((k) => qc.invalidateQueries({ queryKey: [k] }))),
  });
}

export const useCreateUser = () => useAdminMutation((body: AdminUserCreate) => dynamicApi.createUser(body));
export const useUpdateUser = () => useAdminMutation((v: { id: string; body: AdminUserUpdate }) => dynamicApi.updateUser(v.id, v.body));
export const useResetPassword = () => useAdminMutation((v: { id: string; password: string }) => dynamicApi.resetUserPassword(v.id, v.password));
export const useSetUserActive = () => useAdminMutation((v: { id: string; active: boolean }) => dynamicApi.setUserActive(v.id, v.active));

export function useAdminGroups() {
  return useQuery({ queryKey: ['admin-groups'], queryFn: dynamicApi.getAdminGroups });
}

/** Always refetched: the impact must be what the server sees now, not a cached count. */
export function useGroupImpact(id: MaybeRef<number | null>) {
  return useQuery({
    queryKey: ['group-impact', id],
    queryFn: () => dynamicApi.getGroupImpact(toValue(id)!),
    enabled: () => toValue(id) !== null,
    staleTime: 0,
    gcTime: 0,
  });
}

const GROUP_KEYS = ['admin-groups', 'groups', 'admin-users'];
export const useCreateGroup = () => useAdminMutation((v: { name: string; locale: string }) => dynamicApi.createGroup(v), GROUP_KEYS);
export const useRenameGroup = () =>
  useAdminMutation((v: { id: number; name: string; locale: string }) => dynamicApi.renameGroup(v.id, { name: v.name, locale: v.locale }), GROUP_KEYS);
export const useSetGroupMembers = () =>
  useAdminMutation((v: { id: number; userIds: string[] }) => dynamicApi.setGroupMembers(v.id, { userIds: v.userIds }), GROUP_KEYS);
export const useDeleteGroup = () => useAdminMutation((id: number) => dynamicApi.deleteGroup(id), GROUP_KEYS);
