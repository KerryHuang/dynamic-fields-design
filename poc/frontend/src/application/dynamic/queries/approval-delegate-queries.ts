import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { ApprovalDelegateBody } from 'src/domain/dynamic/approval-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** The caller's own delegate setting (null when none). */
export function useMyApprovalDelegate() {
  return useQuery({ queryKey: ['approval-delegate'], queryFn: dynamicApi.getMyApprovalDelegate });
}

export function useSaveMyApprovalDelegate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: ApprovalDelegateBody) => dynamicApi.saveMyApprovalDelegate(body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['approval-delegate'] }),
  });
}

export function useClearMyApprovalDelegate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => dynamicApi.clearMyApprovalDelegate(),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['approval-delegate'] }),
  });
}
