import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { Workflow } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

/** `null` data means the module has no workflow (the server answers 404). */
export function useWorkflowQuery(key: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['workflow', toValue(key)]),
    queryFn: async (): Promise<Workflow | null> => {
      try {
        return await dynamicApi.getWorkflow(toValue(key));
      } catch (e) {
        if ((e as ApiError).status === 404) return null;
        throw e;
      }
    },
    // A refetch would overwrite unsaved edits; saving refreshes the cache.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

/** Saving or removing bumps what the schema carries (`workflow`), so every schema goes stale. */
function useWorkflowWrite<V>(key: MaybeRefOrGetter<string>, fn: (k: string, v: V) => Promise<Workflow | void>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: V) => fn(toValue(key), v),
    onSuccess: (data) => {
      const k = toValue(key);
      qc.setQueryData(['workflow', k], data ?? null);
      return Promise.all([
        qc.invalidateQueries({ queryKey: ['module', k] }),
        qc.invalidateQueries({ queryKey: ['schema'] }),
        qc.invalidateQueries({ queryKey: ['preview-schema'] }),
      ]);
    },
  });
}

export const useSaveWorkflowMutation = (key: MaybeRefOrGetter<string>) =>
  useWorkflowWrite<Workflow>(key, (k, w) => dynamicApi.putWorkflow(k, w));

export const useDeleteWorkflowMutation = (key: MaybeRefOrGetter<string>) =>
  useWorkflowWrite<void>(key, (k) => dynamicApi.deleteWorkflow(k));
