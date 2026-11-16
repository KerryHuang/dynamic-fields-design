import { useQuery } from '@tanstack/vue-query';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** Upload switch, limits and the reason uploads are off; rarely changes, so it is cached for the session. */
export function useAttachmentSettings() {
  return useQuery({
    queryKey: ['attachment-settings'],
    queryFn: dynamicApi.getAttachmentSettings,
    staleTime: 5 * 60_000,
  });
}
