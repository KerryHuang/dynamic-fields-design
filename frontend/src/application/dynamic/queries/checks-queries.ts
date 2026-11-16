import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { ModuleCheck } from 'src/domain/dynamic/events-ledger';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useChecks(moduleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['checks', toValue(moduleKey)]),
    queryFn: () => dynamicApi.getChecks(toValue(moduleKey)),
    retry: false,
  });
}

/** Saving may switch filtering on for referenced fields (reported in `notices`), so the module detail goes stale too. */
export function useSaveCheckMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { check: ModuleCheck; isNew: boolean }) =>
      v.isNew ? dynamicApi.createCheck(v.check) : dynamicApi.updateCheck(v.check.key, v.check),
    onSuccess: () => Promise.all([
      qc.invalidateQueries({ queryKey: ['checks'] }),
      qc.invalidateQueries({ queryKey: ['module'] }),
    ]),
  });
}

export function useDeleteCheckMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => dynamicApi.deleteModuleCheck(key),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['checks'] }),
  });
}
