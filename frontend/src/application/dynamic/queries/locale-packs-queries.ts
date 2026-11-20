import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/vue-query';
import type { PolicyValue } from 'src/domain/dynamic/policy-types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** 語系清單（系統管理員；讀資料庫，不讀快照）。 */
export function useInstalledLocaleAdmin() {
  return useQuery({ queryKey: ['locale-admin'], queryFn: dynamicApi.getLocaleAdmin });
}

/** 任何語系寫入成功後：清單、生效政策（語系清單與文字）、登入選項都要重取，語言選單與文字才會換上新的。 */
const invalidateLocaleViews = (qc: QueryClient) =>
  Promise.all(['locale-admin', 'effective-policies', 'auth-options'].map((k) => qc.invalidateQueries({ queryKey: [k] })));

export function usePreviewLocalePack() {
  return useMutation({ mutationFn: (i: { bytes: ArrayBuffer; fileName: string }) => dynamicApi.previewLocalePack(i.bytes, i.fileName) });
}

export function useApplyLocalePack() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { previewId: string; fileHash: string }) => dynamicApi.applyLocalePack(i.previewId, i.fileHash),
    onSuccess: () => invalidateLocaleViews(qc),
  });
}

export function useRollbackLocalePack() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: (code: string) => dynamicApi.rollbackLocalePack(code), onSuccess: () => invalidateLocaleViews(qc) });
}

export function useClearLocaleOverrides() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: (code: string) => dynamicApi.clearLocaleOverrides(code), onSuccess: () => invalidateLocaleViews(qc) });
}

export function useRemoveLocale() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: (code: string) => dynamicApi.removeLocale(code), onSuccess: () => invalidateLocaleViews(qc) });
}

/** 「現在啟用」：讀目前的 `locale.enabled` 生效清單，加上這個語系再存（政策約束由後端判斷，被擋就丟後端訊息）。 */
export function useEnableLocale() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (code: string) => {
      const policies = await dynamicApi.getSystemPolicies();
      const current = policies.find((p) => p.code === 'locale.enabled')?.effective;
      const list = Array.isArray(current) ? current : [];
      const value: PolicyValue = list.includes(code) ? list : [...list, code];
      return dynamicApi.saveSystemPolicy('locale.enabled', value);
    },
    onSuccess: () => Promise.all([
      ...['system-policies', 'module-policies'].map((k) => qc.invalidateQueries({ queryKey: [k] })),
      invalidateLocaleViews(qc),
    ]),
  });
}
