import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { ApprovalDraftPreviewBody, ApprovalFlowDraftInput, ApprovalFlowInput } from 'src/domain/dynamic/approval-flow-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useApprovalFlows(moduleKey: MaybeRefOrGetter<string>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['approval-flows', toValue(moduleKey)]),
    queryFn: () => dynamicApi.getApprovalFlows(toValue(moduleKey)),
    enabled: computed(() => toValue(enabled)),
  });
}

/** One flow with its graph, layout and draft (the designer page); idle while `key` is null. */
export function useApprovalFlow(key: MaybeRefOrGetter<string | null | undefined>) {
  return useQuery({
    queryKey: computed(() => ['approval-flow', toValue(key)]),
    queryFn: () => dynamicApi.getApprovalFlow(toValue(key)!),
    enabled: computed(() => !!toValue(key)),
  });
}

/**
 * A flow change moves the module's locked states and locked fields, so the workflow response goes stale with it.
 * `flowKeyOf` names the flow whose single-flow cache (`useApprovalFlow`) goes stale too.
 */
function useFlowWrite<V>(moduleKey: MaybeRefOrGetter<string>, fn: (module: string, v: V) => Promise<unknown>, flowKeyOf?: (v: V) => string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: V) => fn(toValue(moduleKey), v),
    onSuccess: (_d, v) => {
      const k = toValue(moduleKey);
      const flowKey = flowKeyOf?.(v);
      return Promise.all([
        qc.invalidateQueries({ queryKey: ['approval-flows', k] }),
        qc.invalidateQueries({ queryKey: ['workflow', k] }),
        ...(flowKey ? [qc.invalidateQueries({ queryKey: ['approval-flow', flowKey] })] : []),
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

// ---- graph designer: draft, publish, dry-run validation ----

export const useCreateFlowDraft = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<ApprovalFlowDraftInput>(moduleKey, (m, input) => dynamicApi.createApprovalFlowDraft(m, input), (input) => input.key);

export const useSaveFlowDraft = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<{ key: string; input: ApprovalFlowDraftInput }>(moduleKey, (_m, v) => dynamicApi.saveApprovalFlowDraft(v.key, v.input), (v) => v.key);

export const useDiscardFlowDraft = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<{ key: string; baseRev: string | null; force?: boolean }>(moduleKey, (_m, v) => dynamicApi.discardApprovalFlowDraft(v.key, v.baseRev, v.force), (v) => v.key);

export const usePublishFlow = (moduleKey: MaybeRefOrGetter<string>) =>
  useFlowWrite<{ key: string; input: ApprovalFlowDraftInput }>(moduleKey, (_m, v) => dynamicApi.publishApprovalFlow(v.key, v.input), (v) => v.key);

/** Dry run, nothing is written so no cache is invalidated. `key` null = a new flow. */
export const useValidateFlow = (moduleKey: MaybeRefOrGetter<string>) =>
  useMutation({ mutationFn: (v: { key: string | null; input: ApprovalFlowDraftInput }) => dynamicApi.validateApprovalFlow(toValue(moduleKey), v.key, v.input) });
