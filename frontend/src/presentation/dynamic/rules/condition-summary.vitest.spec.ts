import { describe, expect, it } from 'vitest';
import type { SchemaField } from 'src/domain/dynamic/types';
import { conditionSummary } from './condition-summary';

const t = (k: string) => ({ 'designer.rules.and': '且', 'designer.rules.or': '或', 'designer.rules.always': '一律符合', 'dynamic.op.eq': '等於', 'dynamic.op.gte': '大於等於' }[k] ?? k);
const fields = [
  { id: 1, key: 'amount', type: 'number', label: { 'zh-TW': '總額' }, config: {}, options: [] },
  { id: 2, key: 'kind', type: 'enum', label: { 'zh-TW': '類型' }, config: {}, options: [{ value: 'a', label: { 'zh-TW': '甲' } }] },
] as unknown as SchemaField[];

describe('conditionSummary', () => {
  it('reads a leaf with field label, operator and option label', () => {
    expect(conditionSummary({ field: 2, op: 'eq', value: 'a' } as never, fields, t, 'zh-TW')).toBe('類型 等於 甲');
  });
  it('joins a group with and, nests a sub group in parentheses', () => {
    const n = { all: [{ field: 1, op: 'gte', value: 100 }, { any: [{ field: 2, op: 'eq', value: 'a' }, { field: 1, op: 'eq', value: 5 }] }] };
    expect(conditionSummary(n as never, fields, t, 'zh-TW')).toBe('總額 大於等於 100 且 (類型 等於 甲 或 總額 等於 5)');
  });
  it('null and empty group read always', () => {
    expect(conditionSummary(null, fields, t, 'zh-TW')).toBe('一律符合');
    expect(conditionSummary({ all: [] } as never, fields, t, 'zh-TW')).toBe('一律符合');
  });
  it('an unknown field falls back to #id', () => {
    expect(conditionSummary({ field: 9, op: 'eq', value: 1 } as never, fields, t, 'zh-TW')).toBe('#9 等於 1');
  });
});
