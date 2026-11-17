import { describe, expect, it } from 'vitest';
import type { CatalogField } from 'src/domain/dynamic/report-types';
import { valueText } from './report-catalog';

const F = (type: string, options?: CatalogField['options']): CatalogField =>
  ({ id: 1, label: 'x', type, canGroup: true, canSum: false, canMinMax: false, isDate: false, options });
const YN = { yes: '是', no: '否' };

describe('valueText', () => {
  it('boolean fields show yes/no instead of the raw true/false', () => {
    expect(valueText(true, F('boolean'), '（停用）', YN)).toBe('是');
    expect(valueText(false, F('boolean'), '（停用）', YN)).toBe('否');
    expect(valueText('true', F('boolean'), '（停用）', YN)).toBe('是');
    expect(valueText('false', F('boolean'), '（停用）', YN)).toBe('否');
  });

  it('without labels, for other types and for unknown fields the raw value stays', () => {
    expect(valueText(true, F('boolean'), '（停用）')).toBe('true');
    expect(valueText(true, F('text'), '（停用）', YN)).toBe('true');
    expect(valueText('ACME', undefined, '（停用）', YN)).toBe('ACME');
  });

  it('options still resolve to labels and disabled ones are marked', () => {
    const f = F('enum', [{ id: 'opt_1', label: '進行中', active: true }, { id: 'opt_2', label: '舊', active: false }]);
    expect(valueText(['opt_1', 'opt_2'], f, '（停用）', YN)).toBe('進行中, 舊（停用）');
  });
});
