import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { toValue, type MaybeRef } from 'vue';
import type { ReportDateRange } from 'src/domain/dynamic/report-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useReports() {
  return useQuery({ queryKey: ['reports'], queryFn: dynamicApi.getReports });
}

export function useReport(id: MaybeRef<number>) {
  return useQuery({ queryKey: ['report', id], queryFn: () => dynamicApi.getReport(toValue(id)) });
}

/** 結果每次進頁都重算：報表依賴的記錄隨時在變，不吃快取。 */
export function useRunReport(id: MaybeRef<number>, locale: MaybeRef<string>) {
  return useQuery({
    queryKey: ['report-run', id, locale],
    queryFn: () => dynamicApi.runReport(toValue(id), null, toValue(locale)),
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });
}

/** 下鑽：groupKeys 為 null 時不查。 */
export function useDrillReport(id: MaybeRef<number>, groupKeys: MaybeRef<string[] | null>, locale: MaybeRef<string>) {
  return useQuery({
    queryKey: ['report-drill', id, groupKeys, locale],
    queryFn: () => dynamicApi.drillReport(toValue(id), toValue(groupKeys)!, null, toValue(locale)),
    enabled: () => toValue(groupKeys) !== null,
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });
}

export function useCopyReport() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.copyReport(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['reports'] }),
  });
}

export function useDeleteReport() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.deleteReport(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['reports'] }),
  });
}

export function useSetReportShares() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: number; groupIds: number[] }) => dynamicApi.setReportShares(v.id, v.groupIds),
    onSuccess: (_d, v) => Promise.all([qc.invalidateQueries({ queryKey: ['reports'] }), qc.invalidateQueries({ queryKey: ['report', v.id] })]),
  });
}
