import { useQuery } from '@tanstack/vue-query';
import type { Ref } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useModuleSchema(key: Ref<string>) {
  return useQuery({
    queryKey: ['schema', key],
    queryFn: () => dynamicApi.getModuleSchema(key.value),
    staleTime: 10 * 60 * 1000,
  });
}

/** Designer preview: the key carries the module version so every edit refetches, even while the tab is kept alive. */
export function usePreviewSchema(key: Ref<string>, version: Ref<number>) {
  return useQuery({
    queryKey: ['preview-schema', key, version],
    queryFn: () => dynamicApi.getPreviewSchema(key.value),
  });
}
