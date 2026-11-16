import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/vue-query';
import { dynamicApi, type OpenAppBody } from 'src/services/api/dynamic-api';

/** Opening/closing changes module statuses, so menu, module list and every cached schema (readOnly) go stale. */
const invalidateAll = (qc: QueryClient) => Promise.all(
  ['apps', 'app-packages', 'menu', 'modules', 'schema'].map((k) => qc.invalidateQueries({ queryKey: [k] })));

export function useAppPackages() {
  return useQuery({ queryKey: ['app-packages'], queryFn: dynamicApi.getAppPackages });
}

export function useApps() {
  return useQuery({ queryKey: ['apps'], queryFn: dynamicApi.getApps });
}

export function useImportAppPackage() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (json: string) => dynamicApi.importAppPackage(json),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['app-packages'] }),
  });
}

export function usePreviewOpenApp() {
  return useMutation({ mutationFn: (v: { id: string; body: OpenAppBody }) => dynamicApi.previewOpenApp(v.id, v.body) });
}

export function useOpenApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: string; body: OpenAppBody }) => dynamicApi.openApp(v.id, v.body),
    onSuccess: () => invalidateAll(qc),
  });
}

export function useCloseApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { key: string; cascade: boolean }) => dynamicApi.closeApp(v.key, v.cascade),
    onSuccess: () => invalidateAll(qc),
  });
}

export function useReopenApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => dynamicApi.reopenApp(key),
    onSuccess: () => invalidateAll(qc),
  });
}
