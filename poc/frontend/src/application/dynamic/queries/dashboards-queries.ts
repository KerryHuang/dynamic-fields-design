import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { toValue, type MaybeRef } from 'vue';
import type { DashboardDetail, DashboardLayout } from 'src/domain/dynamic/dashboard-types';
import type { ReportDateRange } from 'src/domain/dynamic/report-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useDashboards() {
  return useQuery({ queryKey: ['dashboards'], queryFn: dynamicApi.getDashboards, retry: false });
}

/** id 為 null 時不查。版面與個人版隨時可能被改，每次進頁重取。 */
export function useDashboard(id: MaybeRef<number | null>) {
  return useQuery({
    queryKey: ['dashboard', id],
    queryFn: () => dynamicApi.getDashboard(toValue(id)!),
    enabled: () => toValue(id) !== null,
    staleTime: 0,
    retry: false,
  });
}

/** 卡片各自查詢、各自失敗；結果每次都重算，不吃快取。 */
export function useRunCard(
  dashboardId: MaybeRef<number>, cardId: MaybeRef<string>, filterRange: MaybeRef<ReportDateRange | null>, locale: MaybeRef<string>,
  signature: MaybeRef<string> = '',
) {
  return useQuery({
    queryKey: ['dashboard-card', dashboardId, cardId, filterRange, locale, signature],
    queryFn: () => dynamicApi.runCard(toValue(dashboardId), toValue(cardId), toValue(filterRange), toValue(locale)),
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });
}

export function useDrillCard(
  dashboardId: MaybeRef<number>, cardId: MaybeRef<string>, groupKeys: MaybeRef<string[] | null>,
  filterRange: MaybeRef<ReportDateRange | null>, locale: MaybeRef<string>,
) {
  return useQuery({
    queryKey: ['dashboard-drill', dashboardId, cardId, groupKeys, filterRange, locale],
    queryFn: () => dynamicApi.drillCard(toValue(dashboardId), toValue(cardId), toValue(groupKeys)!, toValue(filterRange), toValue(locale)),
    enabled: () => toValue(groupKeys) !== null,
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });
}

function useStoreDetail() {
  const qc = useQueryClient();
  return (detail: DashboardDetail) => {
    qc.setQueryData(['dashboard', detail.id], detail);
    return qc.invalidateQueries({ queryKey: ['dashboards'] });
  };
}

export function useSaveDashboardLayout() {
  const store = useStoreDetail();
  return useMutation({
    mutationFn: (v: { id: number; layout: DashboardLayout; name?: string }) => dynamicApi.saveDashboardLayout(v.id, v.layout, v.name),
    onSuccess: store,
  });
}

export function useRestoreDashboard() {
  const store = useStoreDetail();
  return useMutation({ mutationFn: (id: number) => dynamicApi.restoreDashboard(id), onSuccess: store });
}

export function useCreateDashboard() {
  const store = useStoreDetail();
  return useMutation({ mutationFn: (v: { name: string; layout: DashboardLayout }) => dynamicApi.createDashboard(v.name, v.layout), onSuccess: store });
}

export function useDeleteDashboard() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.deleteDashboard(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['dashboards'] }),
  });
}

// ---- 系統管理：指派版 ----

export function useAdminDashboards() {
  return useQuery({ queryKey: ['admin-dashboards'], queryFn: dynamicApi.adminGetDashboards, retry: false });
}

export function useAdminDashboard(id: MaybeRef<number | null>) {
  return useQuery({
    queryKey: ['admin-dashboard', id],
    queryFn: () => dynamicApi.adminGetDashboard(toValue(id)!),
    enabled: () => toValue(id) !== null,
    staleTime: 0,
    retry: false,
  });
}

/** 指派版變了，使用者端的清單與詳情也要重取。 */
function useInvalidateAdmin() {
  const qc = useQueryClient();
  return () => Promise.all([
    qc.invalidateQueries({ queryKey: ['admin-dashboards'] }),
    qc.invalidateQueries({ queryKey: ['admin-dashboard'] }),
    qc.invalidateQueries({ queryKey: ['dashboards'] }),
    qc.invalidateQueries({ queryKey: ['dashboard'] }),
  ]);
}

export function useAdminCreateDashboard() {
  const invalidate = useInvalidateAdmin();
  return useMutation({ mutationFn: (v: { name: string; layout: DashboardLayout }) => dynamicApi.adminCreateDashboard(v.name, v.layout), onSuccess: invalidate });
}

export function useAdminUpdateDashboard() {
  const invalidate = useInvalidateAdmin();
  return useMutation({
    mutationFn: (v: { id: number; name: string; layout: DashboardLayout; version: number }) =>
      dynamicApi.adminUpdateDashboard(v.id, v.name, v.layout, v.version),
    onSuccess: invalidate,
  });
}

export function useAdminDeleteDashboard() {
  const invalidate = useInvalidateAdmin();
  return useMutation({ mutationFn: (id: number) => dynamicApi.adminDeleteDashboard(id), onSuccess: invalidate });
}

export function useAdminSetAssignments() {
  const invalidate = useInvalidateAdmin();
  return useMutation({
    mutationFn: (v: { id: number; groupIds: number[] }) => dynamicApi.adminSetDashboardAssignments(v.id, v.groupIds),
    onSuccess: invalidate,
  });
}

export function useAdminReorderDashboards() {
  const invalidate = useInvalidateAdmin();
  return useMutation({ mutationFn: (ids: number[]) => dynamicApi.adminReorderDashboards(ids), onSuccess: invalidate });
}
