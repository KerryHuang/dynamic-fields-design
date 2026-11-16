import { describe, expect, it } from 'vitest';
import type { DynamicRecord, SchemaDetail, SchemaField } from 'src/domain/dynamic/types';
import { buildDetailPayload, newRowState, rowErrors, toRowStates, validateRows, type DetailRowState } from './detail-rows';

const field = (id: number, type: SchemaField['type'], label: string): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});
const detail: SchemaDetail = {
  moduleKey: 'line', name: { 'zh-TW': '明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_1',
  fields: [field(1, 'text', '品名'), field(2, 'decimal', '數量')], rules: [],
};
const rec = (id: number, values: Record<string, unknown>): DynamicRecord => ({
  id, moduleKey: 'line', title: '', values, references: {}, hostKey: null, ownerUserId: 'a',
  createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: `v${id}`,
});
const newRow = (values: Record<string, unknown>): DetailRowState => ({ uid: `n${Math.random()}`, values, deleted: false });

describe('detail-rows', () => {
  it('new row payload has values only', () => {
    const { items } = buildDetailPayload([newRow({ f_1: '螺絲', f_2: 3 })]);
    expect(items).toEqual([{ values: { f_1: '螺絲', f_2: 3 } }]);
  });

  it('changed row sends changed fields with id and rowVersion', () => {
    const [row] = toRowStates([rec(5, { f_1: '螺絲', f_2: 3 })]);
    row!.values.f_2 = 4;
    expect(buildDetailPayload([row!]).items).toEqual([{ id: 5, rowVersion: 'v5', values: { f_2: 4 } }]);
  });

  it('deleted existing row sends _delete', () => {
    const [row] = toRowStates([rec(5, { f_1: '螺絲' })]);
    row!.deleted = true;
    row!.values.f_1 = 'changed before delete';
    expect(buildDetailPayload([row!]).items).toEqual([{ id: 5, rowVersion: 'v5', _delete: true }]);
  });

  it('new then deleted row omitted', () => {
    const row = { ...newRow({ f_1: 'x' }), deleted: true };
    expect(buildDetailPayload([row])).toEqual({ items: [], uidAt: [] });
  });

  it('unchanged row omitted', () => {
    const rows = toRowStates([rec(5, { f_1: '螺絲', f_2: 3 })]);
    rows[0]!.values.f_3 = null; // a key absent from the stored values but null in the model is not a change
    expect(buildDetailPayload(rows).items).toEqual([]);
  });

  it('rowErrors maps payload index back to row uid', () => {
    const rows = toRowStates([rec(5, { f_1: 'a' }), rec(6, { f_1: 'b' }), rec(7, { f_1: 'c' })]);
    rows[0]!.deleted = true; // payload 0
    rows[2]!.values.f_1 = 'cc'; // payload 1; row 6 is unchanged and not sent
    const { uidAt } = buildDetailPayload(rows);
    const map = rowErrors({
      'details.line[1].f_1': ['超過 255 個字元'],
      'details.line[0]._record': ['此記錄目前不可修改'],
      'details.other[0].f_1': ['別的明細'],
      'details.line': ['明細最多 200 列'],
      f_1: ['表頭'],
    }, 'line', uidAt);
    expect([...map.entries()]).toEqual([
      [rows[2]!.uid, { f_1: '超過 255 個字元' }],
      [rows[0]!.uid, { _record: '此記錄目前不可修改' }],
    ]);
  });

  it('validateRows flags invalid number', () => {
    const bad = newRow({ f_1: 'x', f_2: 'abc' });
    const good = newRow({ f_1: 'y', f_2: 1 });
    const map = validateRows(detail, [good, bad]);
    expect([...map.entries()]).toEqual([[bad.uid, { f_2: '必須是數字' }]]);
  });
});

describe('newRowState parent default', () => {
  const headerDate = field(50, 'date', '交期');
  const lineDetail: SchemaDetail = {
    ...detail,
    fields: [field(1, 'text', '品名'), { ...field(3, 'date', '交期'), config: { ...field(3, 'date', '').config, parentDefaultFieldId: 50 } }],
  };
  it('takes the header value of the referenced field', () => {
    const row = newRowState(lineDetail, { f_50: '2026-12-01' }, [headerDate]);
    expect(row.values).toEqual({ f_1: null, f_3: '2026-12-01' });
  });
  it('is null when the header has no value or the field is unknown', () => {
    expect(newRowState(lineDetail, { f_50: null }, [headerDate]).values.f_3).toBeNull();
    expect(newRowState(lineDetail, { f_50: '2026-12-01' }, []).values.f_3).toBeNull();
    expect(newRowState(lineDetail).values.f_3).toBeNull();
  });
});
