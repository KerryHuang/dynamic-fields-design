import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { LedgerBody, LedgerConfig } from 'src/domain/dynamic/events-ledger';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

/** `null` data means the module is not a ledger module (the server answers 404). */
export function useLedgerQuery(moduleKey: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => ['ledger', toValue(moduleKey)]),
    queryFn: async (): Promise<LedgerConfig | null> => {
      try {
        return await dynamicApi.getLedger(toValue(moduleKey));
      } catch (e) {
        if ((e as ApiError).status === 404) return null;
        throw e;
      }
    },
    // A refetch would overwrite unsaved edits; saving refreshes the cache.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

/** The schema carries `ledger` and the edit/delete flags, so every schema goes stale when the setting changes. */
function useLedgerWrite<V>(moduleKey: MaybeRefOrGetter<string>, fn: (k: string, v: V) => Promise<LedgerConfig | void>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: V) => fn(toValue(moduleKey), v),
    onSuccess: async (data) => {
      const k = toValue(moduleKey);
      if (data) qc.setQueryData(['ledger', k], data);
      else qc.setQueryData(['ledger', k], null);
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['module', k] }),
        qc.invalidateQueries({ queryKey: ['schema'] }),
        qc.invalidateQueries({ queryKey: ['preview-schema'] }),
        qc.invalidateQueries({ queryKey: ['records', k] }),
      ]);
    },
  });
}

export const useSaveLedgerMutation = (moduleKey: MaybeRefOrGetter<string>) =>
  useLedgerWrite<LedgerBody>(moduleKey, (k, b) => dynamicApi.putLedger(k, b));

export const useDeleteLedgerMutation = (moduleKey: MaybeRefOrGetter<string>) =>
  useLedgerWrite<void>(moduleKey, (k) => dynamicApi.deleteLedger(k));

/** Reverse button and the 'reverses / reversed by' links; always fresh, since reversing elsewhere changes it. */
export function useRecordLedger(id: MaybeRefOrGetter<number>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['record-ledger', toValue(id)]),
    queryFn: () => dynamicApi.getRecordLedger(toValue(id)),
    enabled: computed(() => Number.isFinite(toValue(id)) && toValue(enabled)),
    retry: false,
  });
}

/** Records this record's events created in other modules. */
export function useRecordEffects(id: MaybeRefOrGetter<number>) {
  return useQuery({
    queryKey: computed(() => ['record-effects', toValue(id)]),
    queryFn: () => dynamicApi.getRecordEffects(toValue(id)),
    enabled: computed(() => Number.isFinite(toValue(id))),
    retry: false,
  });
}

/** Reversing adds a ledger entry and changes the original's `reversedBy`; events may also post elsewhere. */
export function useReverseRecord() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: number; idempotencyKey?: string }) => dynamicApi.reverseRecord(v.id, {}, v.idempotencyKey),
    onSuccess: (rec, v) => Promise.all([
      qc.invalidateQueries({ queryKey: ['record-ledger'] }),
      qc.invalidateQueries({ queryKey: ['record-effects'] }),
      qc.invalidateQueries({ queryKey: ['record', v.id] }),
      qc.invalidateQueries({ queryKey: ['records'] }),
      qc.invalidateQueries({ queryKey: ['related'] }),
      qc.invalidateQueries({ queryKey: ['related-paged'] }),
      qc.removeQueries({ queryKey: ['record', rec.id] }),
    ]),
  });
}
