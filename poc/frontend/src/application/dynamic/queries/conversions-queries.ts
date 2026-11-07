import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import type { Conversion, ConvertBody } from 'src/domain/dynamic/types';
import { retryUnlessClientError } from 'src/application/dynamic/queries/schema-queries';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useConversions() {
  return useQuery({ queryKey: ['conversions'], queryFn: dynamicApi.getConversions });
}

/** A new conversion is POSTed, an existing one (editing) PUT by its key. */
export function useSaveConversionMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { conversion: Conversion; isNew: boolean }) =>
      v.isNew ? dynamicApi.createConversion(v.conversion) : dynamicApi.updateConversion(v.conversion.key, v.conversion),
    onSuccess: () => Promise.all([
      qc.invalidateQueries({ queryKey: ['conversions'] }),
      qc.invalidateQueries({ queryKey: ['record-conversions'] }),
    ]),
  });
}

export function useDeleteConversionMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => dynamicApi.deleteConversion(key),
    onSuccess: () => Promise.all([
      qc.invalidateQueries({ queryKey: ['conversions'] }),
      qc.invalidateQueries({ queryKey: ['record-conversions'] }),
    ]),
  });
}

/** What can be converted from one record, with remaining quantities; always fresh because converting elsewhere changes it. */
export function useRecordConversions(id: Ref<number>) {
  return useQuery({
    queryKey: ['record-conversions', id],
    queryFn: () => dynamicApi.getRecordConversions(id.value),
    enabled: computed(() => Number.isFinite(id.value)),
  });
}

/** Required target fields the user must fill before converting; asked each time the dialog opens, since the source may have changed. */
export function useConversionInputs(id: Ref<number>, key: Ref<string>, enabled: Ref<boolean>) {
  return useQuery({
    queryKey: ['conversion-inputs', id, key],
    queryFn: () => dynamicApi.getConversionInputs(id.value, key.value),
    enabled,
    staleTime: 0,
    gcTime: 0,
    retry: retryUnlessClientError,
  });
}

/** Converting changes the source (RowVersion, remaining) and creates a record in the target module. */
export function useConvertRecord() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: number; key: string; body: ConvertBody }) => dynamicApi.convertRecord(v.id, v.key, v.body),
    onSuccess: (rec, v) => Promise.all([
      qc.invalidateQueries({ queryKey: ['record-conversions', v.id] }),
      qc.invalidateQueries({ queryKey: ['record', v.id] }),
      qc.invalidateQueries({ queryKey: ['records'] }),
      qc.invalidateQueries({ queryKey: ['related'] }),
      qc.invalidateQueries({ queryKey: ['related-paged'] }),
      qc.invalidateQueries({ queryKey: ['record-history', v.id] }),
      qc.removeQueries({ queryKey: ['record', rec.id] }),
    ]),
  });
}
