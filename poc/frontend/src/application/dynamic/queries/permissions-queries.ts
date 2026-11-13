import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { ModulePermissions } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';

export function useUsers(opts: { enabled?: MaybeRefOrGetter<boolean> } = {}) {
  return useQuery({ queryKey: ['users'], queryFn: dynamicApi.getUsers, staleTime: Infinity, enabled: computed(() => toValue(opts.enabled ?? true)) });
}

export function useMe() {
  const identity = useIdentityStore();
  return useQuery({ queryKey: computed(() => ['me', identity.userId]), queryFn: dynamicApi.getMe });
}

export function useModulePermissions(key: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['module-permissions', toValue(key)]),
    queryFn: () => dynamicApi.getModulePermissions(toValue(key)),
    // Refetches would overwrite unsaved edits; saving updates the cache via setQueryData.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
}

export function useGroups(opts: { enabled?: MaybeRefOrGetter<boolean> } = {}) {
  return useQuery({ queryKey: ['groups'], queryFn: dynamicApi.getGroups, enabled: computed(() => toValue(opts.enabled ?? true)) });
}

export function useSaveModulePermissions(key: MaybeRefOrGetter<string>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: ModulePermissions) => dynamicApi.saveModulePermissions(toValue(key), body),
    onSuccess: (data) => qc.setQueryData(['module-permissions', toValue(key)], data),
  });
}
