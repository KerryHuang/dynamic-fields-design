import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { TaskRuleInput } from 'src/domain/dynamic/task-rule-draft';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useTaskRulesQuery(moduleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['task-rules', toValue(moduleKey)]),
    queryFn: () => dynamicApi.getTaskRules(toValue(moduleKey)),
  });
}

/** `ruleKey` set = update that rule (the key itself never changes); absent = create. */
export function useSaveTaskRule(moduleKey: MaybeRefOrGetter<string>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { ruleKey?: string; body: TaskRuleInput }) => i.ruleKey
      ? dynamicApi.updateTaskRule(toValue(moduleKey), i.ruleKey, i.body)
      : dynamicApi.createTaskRule(toValue(moduleKey), i.body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['task-rules', toValue(moduleKey)] }),
  });
}

export function useDeleteTaskRule(moduleKey: MaybeRefOrGetter<string>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (ruleKey: string) => dynamicApi.deleteTaskRule(toValue(moduleKey), ruleKey),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['task-rules', toValue(moduleKey)] }),
  });
}

/** 恢復被系統自動暫停的規則（維運設計 §17.2.7）；回應是更新後的規則（autoPause 為 null、帶 lastPause）。 */
export function useResumeTaskRule(moduleKey: MaybeRefOrGetter<string>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (ruleKey: string) => dynamicApi.resumeTaskRule(toValue(moduleKey), ruleKey),
    onSettled: () => qc.invalidateQueries({ queryKey: ['task-rules', toValue(moduleKey)] }),
  });
}
