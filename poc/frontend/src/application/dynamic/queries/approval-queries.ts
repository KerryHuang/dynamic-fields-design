import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type {
  ApprovalBox, ApprovalInboxFilter, ApproveBody, RejectBody, SubmitApprovalBody, WithdrawBody,
} from 'src/domain/dynamic/approval-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { retryUnlessClientError } from './schema-queries';

/**
 * The record, its approval block, its history, the inbox lists and the bell all change with an approval action.
 * A transition it triggers also moves the list, task rules and events, so those go stale too.
 */
function invalidateApproval(qc: QueryClient, recordId: number, moduleKey?: string) {
  return Promise.all([
    ...(moduleKey ? [qc.invalidateQueries({ queryKey: ['records', moduleKey] })] : []),
    qc.invalidateQueries({ queryKey: ['record-tasks', recordId] }),
    qc.invalidateQueries({ queryKey: ['record-effects', recordId] }),
    qc.invalidateQueries({ queryKey: ['record-ledger', recordId] }),
    qc.invalidateQueries({ queryKey: ['record', recordId] }),
    qc.invalidateQueries({ queryKey: ['record-approval', recordId] }),
    qc.invalidateQueries({ queryKey: ['record-history', recordId] }),
    qc.invalidateQueries({ queryKey: ['approval-inbox'] }),
    qc.invalidateQueries({ queryKey: ['notifications'] }),
  ]);
}

export function useRecordApproval(recordId: MaybeRefOrGetter<number>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['record-approval', toValue(recordId)]),
    queryFn: () => dynamicApi.getRecordApproval(toValue(recordId)),
    enabled: computed(() => toValue(enabled) && Number.isFinite(toValue(recordId))),
    retry: retryUnlessClientError,
  });
}

/** Always asks the server again: who would approve depends on the organisation at this moment. */
export function useApprovalPreview(recordId: MaybeRefOrGetter<number>, to: MaybeRefOrGetter<number>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['approval-preview', toValue(recordId), toValue(to)]),
    queryFn: () => dynamicApi.previewApproval(toValue(recordId), toValue(to)),
    enabled: computed(() => toValue(enabled)),
    staleTime: 0,
    gcTime: 0,
    retry: retryUnlessClientError,
  });
}

export function useSubmitApproval() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { recordId: number; moduleKey: string; body: SubmitApprovalBody; idempotencyKey?: string }) =>
      dynamicApi.submitApproval(i.recordId, i.body, i.idempotencyKey),
    onSuccess: (_r, i) => invalidateApproval(qc, i.recordId, i.moduleKey),
  });
}

export function useApproveTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { taskId: number; recordId: number; moduleKey: string; body: ApproveBody; idempotencyKey?: string }) =>
      dynamicApi.approveTask(i.taskId, i.body, i.idempotencyKey),
    onSuccess: (_r, i) => invalidateApproval(qc, i.recordId, i.moduleKey),
  });
}

export function useRejectTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { taskId: number; recordId: number; moduleKey: string; body: RejectBody; idempotencyKey?: string }) =>
      dynamicApi.rejectTask(i.taskId, i.body, i.idempotencyKey),
    onSuccess: (_r, i) => invalidateApproval(qc, i.recordId, i.moduleKey),
  });
}

export function useWithdrawApproval() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { instanceId: number; recordId: number; moduleKey: string; body: WithdrawBody; idempotencyKey?: string }) =>
      dynamicApi.withdrawApproval(i.instanceId, i.body, i.idempotencyKey),
    onSuccess: (_r, i) => invalidateApproval(qc, i.recordId, i.moduleKey),
  });
}

export function useApprovalInbox(box: MaybeRefOrGetter<ApprovalBox>, filter: MaybeRefOrGetter<ApprovalInboxFilter>, page: MaybeRefOrGetter<number>) {
  return useQuery({
    queryKey: computed(() => ['approval-inbox', toValue(box), toValue(filter), toValue(page)]),
    queryFn: () => dynamicApi.getApprovalInbox(toValue(box), toValue(filter), toValue(page)),
    placeholderData: keepPreviousData,
  });
}
