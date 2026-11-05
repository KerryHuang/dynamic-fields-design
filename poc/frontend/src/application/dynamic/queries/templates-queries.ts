import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { dynamicApi, type InstantiateBody } from 'src/services/api/dynamic-api';

export function useTemplates() {
  return useQuery({ queryKey: ['templates'], queryFn: dynamicApi.getTemplates });
}

/** New modules show up in the module list (and draft modules are not in the menu, but the schema caches may be stale). */
export function useInstantiateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { key: string; body: InstantiateBody }) => dynamicApi.instantiateTemplate(v.key, v.body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['modules'] }),
  });
}

export function useImportTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (json: string) => dynamicApi.importTemplate(json),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['templates'] }),
  });
}
