import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';

export function useModules() {
  return useQuery({ queryKey: ['modules'], queryFn: dynamicApi.getModules });
}

export function useMenu() {
  const identity = useIdentityStore();
  return useQuery({ queryKey: computed(() => ['menu', identity.userId]), queryFn: dynamicApi.getMenu });
}

export function useModuleDetail(key: Ref<string>) {
  return useQuery({
    queryKey: ['module', key],
    queryFn: () => dynamicApi.getModule(key.value),
    enabled: computed(() => !!key.value),
    retry: false,
  });
}

/** Reference fields that point at `key` (rollup source candidates); an empty key disables the query. */
export function useRollupSources(key: Ref<string>) {
  return useQuery({
    queryKey: ['rollup-sources', key],
    queryFn: () => dynamicApi.rollupSources(key.value),
    enabled: computed(() => !!key.value),
    retry: false,
  });
}

/**
 * Wraps one designer write. On success it invalidates the module's GetModule, all schemas, views and record pages (so the generic pages
 * pick up the change) and the module list; `extra` adds more prefixes (e.g. ['menu'] when status changes).
 */
export function useDesignerMutation<V, R>(
  fn: (vars: V) => Promise<R>,
  moduleKey: (vars: V) => string,
  extra: string[] = [],
) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (_r, vars) => {
      const key = moduleKey(vars);
      return Promise.all([
        qc.invalidateQueries({ queryKey: ['module', key] }),
        // all schemas: changing a detail module changes its header's schema
        qc.invalidateQueries({ queryKey: ['schema'] }),
        qc.invalidateQueries({ queryKey: ['views', key] }),
        qc.invalidateQueries({ queryKey: ['records', key] }),
        qc.invalidateQueries({ queryKey: ['modules'] }),
        qc.invalidateQueries({ queryKey: ['rollup-sources'] }),
        ...extra.map((k) => qc.invalidateQueries({ queryKey: [k] })),
      ]);
    },
  });
}
