import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { PolicyValue } from 'src/domain/dynamic/policy-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useSystemPolicies() {
  return useQuery({ queryKey: ['system-policies'], queryFn: dynamicApi.getSystemPolicies });
}

export function useSaveSystemPolicy() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { code: string; value: PolicyValue | null }) => dynamicApi.saveSystemPolicy(i.code, i.value),
    onSuccess: () => Promise.all(['system-policies', 'module-policies', 'effective-policies']
      .map((k) => qc.invalidateQueries({ queryKey: [k] }))),
  });
}

export function useModulePolicies(moduleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['module-policies', toValue(moduleKey)]),
    queryFn: () => dynamicApi.getModulePolicies(toValue(moduleKey)),
  });
}

export function useSaveModulePolicy(moduleKey: MaybeRefOrGetter<string>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { code: string; value: PolicyValue | null }) => dynamicApi.saveModulePolicy(toValue(moduleKey), i.code, i.value),
    onSuccess: () => Promise.all([
      qc.invalidateQueries({ queryKey: ['module-policies', toValue(moduleKey)] }),
      qc.invalidateQueries({ queryKey: ['effective-policies'] }),
    ]),
  });
}

/** 模組（或全系統）的生效值：上限、預設值都從這裡取，前端不另存常數。 */
export function useEffectivePolicies(moduleKey: MaybeRefOrGetter<string | null>) {
  return useQuery({
    queryKey: computed(() => ['effective-policies', toValue(moduleKey) ?? '']),
    queryFn: () => dynamicApi.getEffectivePolicies(toValue(moduleKey)),
    staleTime: 60_000,
  });
}
