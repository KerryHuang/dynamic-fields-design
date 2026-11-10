import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** The server tops up due-soon / overdue reminders while it serves this list, so polling is what makes them show up. */
export function useNotifications() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => dynamicApi.getNotifications(20),
    refetchInterval: 60_000,
    refetchOnWindowFocus: true,
  });
}

export function useMarkNotificationRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.markNotificationRead(id),
    // Not awaited: the caller navigates right after the POST instead of waiting for the list to refetch.
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['notifications'] }); },
  });
}

export function useMarkAllNotificationsRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => dynamicApi.markAllNotificationsRead(),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notifications'] }),
  });
}
