import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { ModuleEvent } from 'src/domain/dynamic/events-ledger';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** Events whose source is this module. */
export function useEvents(sourceModuleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['events', toValue(sourceModuleKey)]),
    queryFn: () => dynamicApi.getEvents(toValue(sourceModuleKey)),
    retry: false,
  });
}

/** A new event is POSTed, an existing one (editing) PUT by its key. Answers with the saved event incl. `warnings`. */
export function useSaveEventMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { event: ModuleEvent; isNew: boolean }) =>
      v.isNew ? dynamicApi.createEvent(v.event) : dynamicApi.updateEvent(v.event.key, v.event),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['events'] }),
  });
}

export function useDeleteEventMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => dynamicApi.deleteEvent(key),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['events'] }),
  });
}
