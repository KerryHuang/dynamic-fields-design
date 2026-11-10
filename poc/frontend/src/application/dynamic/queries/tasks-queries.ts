import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { TaskInput, TaskListFilter, TaskListTab, TaskPatch } from 'src/domain/dynamic/task-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useMyTasks(tab: MaybeRefOrGetter<TaskListTab>, filter: MaybeRefOrGetter<TaskListFilter>, page: MaybeRefOrGetter<number>) {
  return useQuery({
    queryKey: computed(() => ['tasks', toValue(tab), toValue(filter), toValue(page)]),
    queryFn: () => dynamicApi.getMyTasks(toValue(tab), toValue(filter), toValue(page)),
    placeholderData: keepPreviousData,
  });
}

export function useTaskSummary() {
  return useQuery({ queryKey: ['task-summary'], queryFn: dynamicApi.getTaskSummary });
}

/** `id` null (dialog closed) keeps the query off. */
export function useTask(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: computed(() => ['task', toValue(id)]),
    queryFn: () => dynamicApi.getTask(toValue(id)!),
    enabled: computed(() => toValue(id) !== null),
    retry: false,
  });
}

export function useRecordTasks(recordId: MaybeRefOrGetter<number>) {
  return useQuery({
    queryKey: computed(() => ['record-tasks', toValue(recordId)]),
    queryFn: () => dynamicApi.getRecordTasks(toValue(recordId)),
    enabled: computed(() => Number.isFinite(toValue(recordId))),
  });
}

export function useTaskComments(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: computed(() => ['task-comments', toValue(id)]),
    queryFn: () => dynamicApi.getTaskComments(toValue(id)!),
    enabled: computed(() => toValue(id) !== null),
  });
}

/** Typing "@abc" asks the server for at most 20 active users; empty keyword lists the first ones. */
export function useMentionCandidates(keyword: MaybeRefOrGetter<string | null>) {
  return useQuery({
    queryKey: computed(() => ['mention-candidates', toValue(keyword)]),
    queryFn: () => dynamicApi.getMentionCandidates(toValue(keyword) ?? ''),
    enabled: computed(() => toValue(keyword) !== null),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  });
}

/** Every task change touches the lists, the counters, the open task, the record block and the timeline. */
function invalidateTasks(qc: QueryClient, id?: number) {
  return Promise.all([
    qc.invalidateQueries({ queryKey: ['tasks'] }),
    qc.invalidateQueries({ queryKey: ['task-summary'] }),
    qc.invalidateQueries({ queryKey: ['record-tasks'] }),
    ...(id === undefined ? [] : [
      qc.invalidateQueries({ queryKey: ['task', id] }),
      qc.invalidateQueries({ queryKey: ['task-comments', id] }),
    ]),
  ]);
}

export function useCreateTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { recordId?: number; body: TaskInput }) =>
      i.recordId === undefined ? dynamicApi.createTask(i.body) : dynamicApi.createRecordTask(i.recordId, i.body),
    onSuccess: (t) => invalidateTasks(qc, t.id),
  });
}

export function useUpdateTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { id: number; patch: TaskPatch }) => dynamicApi.updateTask(i.id, i.patch),
    onSuccess: (_t, i) => invalidateTasks(qc, i.id),
  });
}

export function useDeleteTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.deleteTask(id),
    onSuccess: (_d, id) => {
      qc.removeQueries({ queryKey: ['task', id] });
      return invalidateTasks(qc);
    },
  });
}

export function useAddTaskComment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { id: number; body: string; files: File[] }) =>
      i.files.length ? dynamicApi.addTaskCommentWithFiles(i.id, i.body, i.files) : dynamicApi.addTaskComment(i.id, i.body),
    onSuccess: (_c, i) => invalidateTasks(qc, i.id),
  });
}

export function useWithdrawTaskComment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { id: number; commentId: number }) => dynamicApi.withdrawTaskComment(i.id, i.commentId),
    onSuccess: (_c, i) => invalidateTasks(qc, i.id),
  });
}
