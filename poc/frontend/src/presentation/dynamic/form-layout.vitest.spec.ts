import { describe, expect, it } from 'vitest';
import type { FieldConfig, FieldType } from 'src/domain/dynamic/types';
import { fieldColClass, isFullWidth } from './form-layout';

const f = (type: FieldType, fullWidth?: boolean) =>
  ({ type, config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, fullWidth } as FieldConfig });

describe('isFullWidth', () => {
  it('null follows the type: textarea is full width, others are not', () => {
    expect(isFullWidth(f('textarea'))).toBe(true);
    expect(isFullWidth(f('text'))).toBe(false);
  });

  it('an explicit setting wins over the type default', () => {
    expect(isFullWidth(f('textarea', false))).toBe(false);
    expect(isFullWidth(f('text', true))).toBe(true);
    expect(isFullWidth(f('integer', false))).toBe(false);
  });
});

describe('fieldColClass', () => {
  it('xs is one column; sm caps at 2, md at 3, lg at 4, each also capped by the block columns', () => {
    expect(fieldColClass(1, false)).toBe('col-12');
    expect(fieldColClass(2, false)).toBe('col-12 col-sm-6');
    expect(fieldColClass(3, false)).toBe('col-12 col-sm-6 col-md-4');
    expect(fieldColClass(4, false)).toBe('col-12 col-sm-6 col-md-4 col-lg-3');
  });

  it('a full-width field takes the whole row whatever the columns', () => {
    for (const columns of [1, 2, 3, 4]) expect(fieldColClass(columns, true)).toBe('col-12');
  });
});
