import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { PrintTemplateDefinition } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * Templates a user can print with; a 403 (no print permission) leaves `data` empty and the print button hidden.
 * `recordId` is passed only for an approver's temporary read (approval grant), which the backend checks per record.
 */
export function usePrintTemplates(moduleKey: MaybeRefOrGetter<string>, recordId?: MaybeRefOrGetter<number | undefined>) {
  return useQuery({
    queryKey: computed(() => ['print-templates', toValue(moduleKey), toValue(recordId) ?? null]),
    queryFn: () => dynamicApi.getPrintTemplates(toValue(moduleKey), toValue(recordId)),
    retry: false,
    staleTime: 60_000,
  });
}

export function usePrintTemplateDefinitions(moduleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['print-template-defs', toValue(moduleKey)]),
    queryFn: () => dynamicApi.getPrintTemplateDefinitions(toValue(moduleKey)),
    retry: false,
    // A refetch would overwrite unsaved edits; saving and deleting refresh the cache.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
}

function useWrite<V>(moduleKey: MaybeRefOrGetter<string>, fn: (key: string, v: V) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: V) => fn(toValue(moduleKey), v),
    onSuccess: () => {
      const k = toValue(moduleKey);
      return Promise.all([
        qc.invalidateQueries({ queryKey: ['print-template-defs', k] }),
        qc.invalidateQueries({ queryKey: ['print-templates', k] }),
      ]);
    },
  });
}

export const useCreatePrintTemplate = (moduleKey: MaybeRefOrGetter<string>) =>
  useWrite<PrintTemplateDefinition>(moduleKey, (k, v) => dynamicApi.createPrintTemplate(k, v));

export const useUpdatePrintTemplate = (moduleKey: MaybeRefOrGetter<string>) =>
  useWrite<PrintTemplateDefinition>(moduleKey, (k, v) => dynamicApi.updatePrintTemplate(k, v));

export const useDeletePrintTemplate = (moduleKey: MaybeRefOrGetter<string>) =>
  useWrite<string>(moduleKey, (k, key) => dynamicApi.deletePrintTemplate(k, key));
