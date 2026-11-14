import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { ApprovalDelegateBody } from 'src/domain/dynamic/approval-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * The caller's own delegate setting and the server's business day. Active / upcoming change when the business day turns:
 * refetch a moment after the server says the next day starts, and on focus (a sleeping laptop may miss the timer).
 */
export function useMyApprovalDelegate() {
  return useQuery({
    queryKey: ['approval-delegate'],
    queryFn: dynamicApi.getMyApprovalDelegate,
    refetchInterval: (q) => (q.state.data ? q.state.data.secondsToNextDay * 1000 + 1000 : false),
    refetchOnWindowFocus: true,
  });
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
