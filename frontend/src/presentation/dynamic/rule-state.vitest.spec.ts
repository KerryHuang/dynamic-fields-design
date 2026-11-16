import { describe, expect, it } from 'vitest';
import type { ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import { isWritable, ruleStateOf } from './rule-state';

const field = (id: number, key: string, type: SchemaField['type']): SchemaField => ({
  id, key, type, label: { 'zh-TW': key }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true },
});

const schema = (rules: ModuleSchema['rules']): ModuleSchema => ({
  key: 'm', programCode: 'M', name: { 'zh-TW': 'M' }, titleFieldKey: null, version: 1,
  permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, collapsed: false, columns: 2, fields: [field(1, 'name', 'text'), field(2, 'total', 'rollup'), field(3, 'note', 'text')] }],
  rules,
});

describe('rule-state', () => {
  it('skips rules referencing rollup when skipComputed', () => {
    const rule = {
      kind: 'required' as const, targets: ['field:3'], options: [],
      condition: { field: 2, op: 'gt' as const, value: 100 },
    };
    const values = { total: 500 };
    expect(ruleStateOf(schema([rule]), values, undefined).required.has(3)).toBe(true);
    expect(ruleStateOf(schema([rule]), values, undefined, { skipComputed: true }).required.has(3)).toBe(false);
  });

  it('isWritable is false for rollup', () => {
    const empty = ruleStateOf(schema([]), {}, undefined);
    expect(isWritable(field(2, 'total', 'rollup'), empty)).toBe(false);
    expect(isWritable(field(3, 'note', 'text'), empty)).toBe(true);
  });
});
