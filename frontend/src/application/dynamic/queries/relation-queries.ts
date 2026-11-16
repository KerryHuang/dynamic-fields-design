import { useQuery } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useEntities() {
  return useQuery({ queryKey: ['entities'], queryFn: () => dynamicApi.entities() });
}

export function useRelatedTabs(recordId: Ref<number>) {
  return useQuery({
    queryKey: ['related', recordId],
    queryFn: () => dynamicApi.related(recordId.value),
    enabled: computed(() => Number.isFinite(recordId.value)),
  });
}

export function useRelatedPaged(recordId: Ref<number>, fieldId: Ref<number | undefined>, page: Ref<number>, size: Ref<number>) {
  return useQuery({
    queryKey: ['related-paged', recordId, fieldId, page, size],
    queryFn: () => dynamicApi.relatedPaged(recordId.value, fieldId.value!, page.value, size.value),
    enabled: computed(() => Number.isFinite(recordId.value) && fieldId.value !== undefined),
    placeholderData: (prev) => prev,
  });
}

export function useRecordByHost(moduleKey: Ref<string>, hostKey: Ref<string | undefined>) {
  return useQuery({
    queryKey: ['record-by-host', moduleKey, hostKey],
    queryFn: () => dynamicApi.recordByHost(moduleKey.value, hostKey.value!),
    enabled: computed(() => !!hostKey.value),
    retry: false,
  });
}
