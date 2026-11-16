import { useQuery, type QueryClient } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** A 4xx (no permission, not found) will not change by asking again; only retry network and server errors. */
export const retryUnlessClientError = (count: number, error: unknown) => {
  const status = (error as { status?: number }).status;
  return count < 3 && !(status !== undefined && status >= 400 && status < 500);
};

export function useModuleSchema(key: Ref<string>, enabled?: Ref<boolean>) {
  return useQuery({
    queryKey: ['schema', key],
    queryFn: () => dynamicApi.getModuleSchema(key.value),
    ...(enabled && { enabled }),
    staleTime: 10 * 60 * 1000,
    retry: retryUnlessClientError,
  });
}

/** The schema as an approver holding only the temporary read right sees it; asked for only after the plain schema was refused. */
export function useRecordScopedSchema(key: Ref<string>, recordId: Ref<number>, enabled: Ref<boolean>) {
  return useQuery({
    queryKey: ['schema', key, 'record', recordId],
    queryFn: () => dynamicApi.getModuleSchema(key.value, recordId.value),
    enabled: computed(() => enabled.value && Number.isFinite(recordId.value)),
    staleTime: 0,
    retry: retryUnlessClientError,
  });
}

/** Designer preview: the key carries the module version so every edit refetches, even while the tab is kept alive. */
export function usePreviewSchema(key: Ref<string>, version: Ref<number>) {
  return useQuery({
    queryKey: ['preview-schema', key, version],
    queryFn: () => dynamicApi.getPreviewSchema(key.value),
  });
}

/** Fetches (or reuses the cached) schema without a component, e.g. to find out which fields a server error belongs to. */
export function fetchModuleSchema(qc: QueryClient, key: string) {
  return qc.fetchQuery({ queryKey: ['schema', key], queryFn: () => dynamicApi.getModuleSchema(key), staleTime: 10 * 60 * 1000 });
}
