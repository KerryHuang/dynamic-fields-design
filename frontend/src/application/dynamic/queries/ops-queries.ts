import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type {
  AuditFilter, HealthCheckUpdate, IncidentFilter, MailSettingsInput, SmtpChannelInput,
} from 'src/domain/ops/ops-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

// 維運監控、稽核、寄信設定（維運設計 §17.3、§17.5）。監控資料會變，所以不設長 staleTime；
// 重試沿用全域設定（不重試），503 databaseUnavailable 立刻顯示整頁提示。
const OPS_KEYS = ['ops-overview', 'ops-health-checks', 'ops-incidents', 'ops-incident'];

export const useOpsOverview = () =>
  useQuery({ queryKey: ['ops-overview'], queryFn: dynamicApi.getOpsOverview, refetchInterval: 60_000 });

export const useHealthChecks = (enabled: MaybeRefOrGetter<boolean> = true) =>
  useQuery({ queryKey: ['ops-health-checks'], queryFn: dynamicApi.getHealthChecks, enabled: computed(() => toValue(enabled)) });

/** 趨勢：最近 N 小時；筆數上限用伺服器的預設（前端不留上限常數）。 */
export function useHealthResults(code: MaybeRefOrGetter<string>, hours: MaybeRefOrGetter<number>, enabled: MaybeRefOrGetter<boolean>) {
  return useQuery({
    queryKey: computed(() => ['ops-health-results', toValue(code), toValue(hours)]),
    queryFn: () => {
      const to = new Date();
      const from = new Date(to.getTime() - toValue(hours) * 3_600_000);
      return dynamicApi.getHealthResults(toValue(code), from.toISOString(), to.toISOString());
    },
    enabled: computed(() => toValue(enabled)),
  });
}

function useOpsMutation<V, R>(fn: (v: V) => Promise<R>, keys = OPS_KEYS) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSettled: () => Promise.all(keys.map((k) => qc.invalidateQueries({ queryKey: [k] }))),
  });
}

export const useUpdateHealthCheck = () => useOpsMutation((v: { code: string; body: HealthCheckUpdate }) => dynamicApi.updateHealthCheck(v.code, v.body));
export const useRunHealthCheck = () => useOpsMutation((code: string) => dynamicApi.runHealthCheck(code), ['ops-overview', 'ops-health-checks', 'ops-health-results']);

export function useIncidents(filter: MaybeRefOrGetter<IncidentFilter>) {
  return useInfiniteQuery({
    queryKey: computed(() => ['ops-incidents', toValue(filter)]),
    queryFn: ({ pageParam }) => dynamicApi.getIncidents(toValue(filter), pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor,
  });
}

export const useIncident = (id: MaybeRefOrGetter<number | null>) =>
  useQuery({
    queryKey: computed(() => ['ops-incident', toValue(id)]),
    queryFn: () => dynamicApi.getIncident(toValue(id)!),
    enabled: computed(() => toValue(id) !== null),
  });

export const useAcknowledgeIncident = () =>
  useOpsMutation((v: { id: number; expectedStatus: string }) => dynamicApi.acknowledgeIncident(v.id, v.expectedStatus));
export const useResolveIncident = () =>
  useOpsMutation((v: { id: number; expectedStatus: string; note?: string }) => dynamicApi.resolveIncident(v.id, v.expectedStatus, v.note));

export const useSmtpChannel = () => useQuery({ queryKey: ['ops-channel'], queryFn: dynamicApi.getSmtpChannel });
export const useSaveSmtpChannel = () => useOpsMutation((b: SmtpChannelInput) => dynamicApi.saveSmtpChannel(b), ['ops-channel', 'ops-overview']);
export const useTestSmtpChannel = () => useOpsMutation(() => dynamicApi.testSmtpChannel(), ['ops-channel', 'ops-overview']);

export const useMailSettings = () => useQuery({ queryKey: ['mail-settings'], queryFn: dynamicApi.getMailSettings });
/**
 * 密碼不進 mutation variables：variables 與結果留在共用的 mutation cache（成功、失敗都留，頁面還在就不回收），明文會一直留著。
 * 呼叫端傳讀密碼欄的函式，送出當下才併進請求主體。
 */
export const useSaveMailSettings = (secret: () => Pick<MailSettingsInput, 'password'>) =>
  useOpsMutation((b: Omit<MailSettingsInput, 'password'>) => dynamicApi.saveMailSettings({ ...b, ...secret() }), ['mail-settings', 'ops-channel', 'ops-overview']);
export const useTestMailSettings = () => useOpsMutation(() => dynamicApi.testMailSettings(), []);

export function useAudit(filter: MaybeRefOrGetter<AuditFilter>) {
  return useInfiniteQuery({
    queryKey: computed(() => ['ops-audit', toValue(filter)]),
    queryFn: ({ pageParam }) => dynamicApi.getAudit(toValue(filter), pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor,
  });
}

export const useAuditEntry = (id: MaybeRefOrGetter<number | null>) =>
  useQuery({
    queryKey: computed(() => ['ops-audit-entry', toValue(id)]),
    queryFn: () => dynamicApi.getAuditEntry(toValue(id)!),
    enabled: computed(() => toValue(id) !== null),
  });

export const useAuditActions = () => useQuery({ queryKey: ['ops-audit-actions'], queryFn: dynamicApi.getAuditActions, staleTime: Infinity });
