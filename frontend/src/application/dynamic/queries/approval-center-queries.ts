import { useQuery } from '@tanstack/vue-query';
import { computed } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * The "all" tab only exists for people holding the Approve right on some module, and the client has no list of that right.
 * One cheap request decides: 403 means no tab. A different failure keeps the tab; its own list then shows the error.
 * The key starts with `approval-inbox`, so every approval action refreshes it too.
 */
export function useApprovalAllAccess() {
  const q = useQuery({
    queryKey: ['approval-inbox', 'all-probe'],
    queryFn: () => dynamicApi.getApprovalInbox('all', {}, 1, 1),
    retry: false,
    staleTime: 60_000,
  });
  const denied = computed(() => q.isError.value && (q.error.value as { status?: number } | null)?.status === 403);
  return { denied, allowed: computed(() => q.isSuccess.value || (q.isError.value && !denied.value)) };
}
