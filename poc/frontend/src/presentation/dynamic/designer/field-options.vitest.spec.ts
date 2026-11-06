import { describe, expect, it } from 'vitest';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import { copySource, copyTarget, fieldOptions, isIntegerField, isQuantityField } from './field-options';

const f = (id: number, type: FieldType, status: DesignerField['status'] = 'Active'): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status, options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});
const module: ModuleDetail = {
  id: 1, key: 'm', programCode: 'P', name: {}, status: 'Active', version: 1,
  blocks: [{ id: 1, label: {}, sequence: 1, collapsed: false, columns: 2, fields: [f(1, 'text'), f(2, 'enum'), f(3, 'formula'), f(4, 'integer', 'Inactive'), f(5, 'decimal')] }],
};

describe('field-options', () => {
  it('lists active fields only, labelled in the locale', () => {
    expect(fieldOptions(module, 'zh-TW')).toEqual([1, 2, 3, 5].map((id) => ({ value: id, label: `欄${id}` })));
    expect(fieldOptions(undefined, 'zh-TW')).toEqual([]);
  });
  it('copy sources exclude enums; targets also exclude computed fields', () => {
    expect(fieldOptions(module, 'zh-TW', copySource).map((o) => o.value)).toEqual([1, 3, 5]);
    expect(fieldOptions(module, 'zh-TW', copyTarget).map((o) => o.value)).toEqual([1, 5]);
  });
  it('quantity fields are integer or decimal; the tracing field is integer', () => {
    expect(fieldOptions(module, 'zh-TW', isQuantityField).map((o) => o.value)).toEqual([5]);
    expect(fieldOptions(module, 'zh-TW', isIntegerField).map((o) => o.value)).toEqual([]);
  });
});
