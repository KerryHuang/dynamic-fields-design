import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import { serializeFilter } from 'src/domain/dynamic/condition';
import type { ConditionNode, DynamicRecord, ViewSort } from 'src/domain/dynamic/types';
import { dynamicApi, type DetailsPayload, type RecordValues } from 'src/services/api/dynamic-api';

export interface RecordsState {
  page: number;
  size: number;
  filter?: ConditionNode;
  sort: ViewSort[];
  q?: string;
}

/** The whole state object is part of the queryKey; `enabled` lets the page wait for the default view. */
export function usePagedRecords(key: Ref<string>, state: Ref<RecordsState>, enabled?: Ref<boolean>) {
  return useQuery({
    queryKey: ['records', key, state],
    queryFn: () => {
      const s = state.value;
      return dynamicApi.getPagedRecords(key.value, {
        currentPage: s.page, pagesSize: s.size, filter: serializeFilter(s.filter), sort: s.sort, q: s.q,
      });
    },
    // keep the previous rows while loading or after a 400, but never across modules
    placeholderData: (prev, prevQuery) => (prevQuery?.queryKey[1] === key.value ? prev : undefined),
    enabled,
  });
}

export function useRecord(id: Ref<number>) {
  return useQuery({
    queryKey: ['record', id],
    queryFn: () => dynamicApi.getRecord(id.value),
    enabled: computed(() => Number.isFinite(id.value)),
  });
}

export function useRecordHistory(id: Ref<number>, page: Ref<number>, size: Ref<number>, locale: Ref<string>) {
  return useQuery({
    queryKey: ['record-history', id, page, locale],
    queryFn: () => dynamicApi.recordHistory(id.value, page.value, size.value, locale.value),
    enabled: computed(() => Number.isFinite(id.value)),
    placeholderData: (prev) => prev,
  });
}

export type SaveRecordInput =
  | { moduleKey: string; id?: undefined; values: RecordValues; hostKey?: string; details?: DetailsPayload; idempotencyKey?: string }
  | { moduleKey: string; id: number; values: RecordValues; rowVersion: string; details?: DetailsPayload; idempotencyKey?: string };

export function useSaveRecord() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: SaveRecordInput): Promise<DynamicRecord> =>
      i.id === undefined ? dynamicApi.createRecord(i.moduleKey, i.values, i.hostKey, i.details, i.idempotencyKey)
      : dynamicApi.updateRecord(i.id, i.values, i.rowVersion, i.details, i.idempotencyKey),
    onSuccess: (rec) => Promise.all([
      qc.invalidateQueries({ queryKey: ['records', rec.moduleKey] }),
      qc.invalidateQueries({ queryKey: ['record', rec.id] }),
      qc.invalidateQueries({ queryKey: ['record-history', rec.id] }),
    ]),
  });
}

export function useCommitImport() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { moduleKey: string; file: File; idempotencyKey?: string }) => dynamicApi.commitImport(i.moduleKey, i.file, i.idempotencyKey),
    onSuccess: (_r, i) => qc.invalidateQueries({ queryKey: ['records', i.moduleKey] }),
  });
}

export function useDeleteRecord() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (i: { moduleKey: string; id: number; rowVersion: string; idempotencyKey?: string }) =>
      dynamicApi.deleteRecord(i.id, i.rowVersion, i.idempotencyKey),
    onSuccess: (_d, i) => Promise.all([
      qc.invalidateQueries({ queryKey: ['records', i.moduleKey] }),
      qc.removeQueries({ queryKey: ['record', i.id] }),
      qc.removeQueries({ queryKey: ['record-history', i.id] }),
    ]),
  });
}
