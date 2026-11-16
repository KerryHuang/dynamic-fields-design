import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { Rule } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useRulesQuery(key: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['rules', toValue(key)]),
    queryFn: () => dynamicApi.getRules(toValue(key)),
    // A refetch would overwrite unsaved edits; saving refreshes the cache.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
}

/** Saving bumps Module.Version, so the module detail and the schema (which carries the rules) go stale too. */
export function useSaveRulesMutation(key: MaybeRefOrGetter<string>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (rules: Rule[]) => dynamicApi.putRules(toValue(key), rules),
    onSuccess: (data) => {
      const k = toValue(key);
      qc.setQueryData(['rules', k], data);
      return Promise.all([
        qc.invalidateQueries({ queryKey: ['module', k] }),
        // all schemas: a detail module's rules also live in its header's schema
        qc.invalidateQueries({ queryKey: ['schema'] }),
      ]);
    },
  });
}
