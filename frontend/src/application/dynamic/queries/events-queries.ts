import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import { hiddenFieldIds } from 'src/domain/dynamic/event-draft';
import type { ModuleEvent } from 'src/domain/dynamic/events-ledger';
import { dynamicApi } from 'src/services/api/dynamic-api';

/** Events whose source is this module; an empty key (not a possible source) sends no request. */
export function useEvents(sourceModuleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['events', toValue(sourceModuleKey)]),
    queryFn: () => dynamicApi.getEvents(toValue(sourceModuleKey)),
    enabled: computed(() => !!toValue(sourceModuleKey)),
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

/** Fields of a module that are hidden for some group; `key` empty means no module picked yet (no request). */
export function useHiddenFieldIds(key: MaybeRefOrGetter<string>) {
  const query = useQuery({
    queryKey: computed(() => ['module-permissions', toValue(key)]),
    queryFn: () => dynamicApi.getModulePermissions(toValue(key)),
    enabled: computed(() => !!toValue(key)),
    // shares the permissions tab's cache entry, which saving there replaces; a refetch would add nothing
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    retry: false,
  });
  return computed(() => hiddenFieldIds(query.data.value));
}
