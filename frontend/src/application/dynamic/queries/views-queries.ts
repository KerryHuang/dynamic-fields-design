import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { Ref } from 'vue';
import type { ListView } from 'src/domain/dynamic/types';
import { dynamicApi, type ViewBody } from 'src/services/api/dynamic-api';

/** Own personal default first, then the shared default; undefined = use the schema's ShowInList fields. */
export function pickDefaultView(views: ListView[]): ListView | undefined {
  return views.find((v) => v.isDefault && !v.shared && v.isOwner) ?? views.find((v) => v.isDefault && v.shared);
}

export function useViews(key: Ref<string>) {
  return useQuery({ queryKey: ['views', key], queryFn: () => dynamicApi.getViews(key.value) });
}

export function useCreateView() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { moduleKey: string; body: ViewBody & { shared: boolean } }) => dynamicApi.createView(i.moduleKey, i.body),
    onSuccess: (_d, i) => qc.invalidateQueries({ queryKey: ['views', i.moduleKey] }),
  });
}

export function useUpdateView() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { moduleKey: string; id: number; body: ViewBody }) => dynamicApi.updateView(i.id, i.body),
    onSuccess: (_d, i) => qc.invalidateQueries({ queryKey: ['views', i.moduleKey] }),
  });
}

export function useDeleteView() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { moduleKey: string; id: number }) => dynamicApi.deleteView(i.id),
    onSuccess: (_d, i) => qc.invalidateQueries({ queryKey: ['views', i.moduleKey] }),
  });
}
