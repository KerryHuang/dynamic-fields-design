import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { watch } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** Polls while the server is generating; when a run finishes, the new modules appear in the menu and module list. */
export function useDemoDataStatus() {
  const qc = useQueryClient();
  const query = useQuery({
    queryKey: ['demo-data'],
    queryFn: dynamicApi.getDemoDataStatus,
    refetchInterval: (q) => (q.state.data?.state === 'running' ? 2000 : false),
  });
  watch(() => query.data.value?.state, (now, before) => {
    if (before === 'running' && now === 'generated') {
      void qc.invalidateQueries({ queryKey: ['menu'] });
      void qc.invalidateQueries({ queryKey: ['modules'] });
    }
  });
  return query;
}

/** Success and 409 both change the status (running / conflict), so refetch either way. */
export function useGenerateDemoData() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: dynamicApi.generateDemoData,
    onSettled: () => qc.invalidateQueries({ queryKey: ['demo-data'] }),
  });
}
