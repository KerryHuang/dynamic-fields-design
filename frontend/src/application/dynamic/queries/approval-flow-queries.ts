import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { ApprovalDraftPreviewBody, ApprovalFlowInput } from 'src/domain/dynamic/approval-flow-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useApprovalFlows(moduleKey: MaybeRefOrGetter<string>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['approval-flows', toValue(moduleKey)]),
    queryFn: () => dynamicApi.getApprovalFlows(toValue(moduleKey)),
    enabled: computed(() => toValue(enabled)),
  });
}

/** A flow change moves the module's locked states and locked fields, so the workflow response goes stale with it. */
function useFlowWrite<V>(moduleKey: MaybeRefOrGetter<string>, fn: (module: string, v: V) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: V) => fn(toValue(moduleKey), v),
    onSuccess: () => {
      const k = toValue(moduleKey);
      return Promise.all([
        qc.invalidateQueries({ queryKey: ['approval-flows', k] }),
        qc.invalidateQueries({ queryKey: ['workflow', k] }),
      ]);
    },
  });
}

export const useSaveApprovalFlow = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<{ key?: string; input: ApprovalFlowInput }>(moduleKey, (m, v) =>
    (v.key ? dynamicApi.updateApprovalFlow(v.key, v.input) : dynamicApi.createApprovalFlow(m, v.input)));

export const useDeleteApprovalFlow = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<string>(moduleKey, (_m, key) => dynamicApi.deleteApprovalFlow(key));

export const useReorderApprovalFlows = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<{ toOptionId: number; keys: string[] }>(moduleKey, (m, v) => dynamicApi.reorderApprovalFlows(m, v.toOptionId, v.keys));

/** Trial of the edited flow: nothing is written, so no cache is invalidated. */
export const usePreviewApprovalDraft = (moduleKey: MaybeRefOrGetter<string>) =>
  useMutation({ mutationFn: (body: ApprovalDraftPreviewBody) => dynamicApi.previewApprovalDraft(toValue(moduleKey), body) });

/** Record search for the trial (title search, first 20). */
export const useSearchTrialRecords = (moduleKey: MaybeRefOrGetter<string>) =>
  useMutation({ mutationFn: (q: string) => dynamicApi.getPagedRecords(toValue(moduleKey), { currentPage: 1, pagesSize: 20, q: q || undefined }) });
