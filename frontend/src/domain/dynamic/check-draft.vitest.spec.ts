import { describe, expect, it } from 'vitest';
import { buildCheck, draftFromCheck, emptyCheckDraft, groupCheckErrors, limitText } from './check-draft';
import type { ModuleCheck } from './events-ledger';

const stock: ModuleCheck = {
  key: 'stock-not-negative', name: { 'zh-TW': '庫存不得為負' }, moduleKey: 'stock-move', active: true,
  definition: {
    groupBy: [51, 52], function: 'sum', fieldId: 55, where: { all: [{ field: 56, op: 'eq', value: 'opt_61' }] },
    assert: { op: 'gte', limit: { const: 0 } }, message: { 'zh-TW': '庫存不足，不能出貨' },
  },
};

describe('check draft', () => {
  it('round-trips a saved check unchanged', () => {
    expect(buildCheck(draftFromCheck(stock), 'stock-move')).toEqual(stock);
  });

  it('round-trips the other two limit kinds', () => {
    const viaCheck: ModuleCheck = {
      ...stock, definition: { ...stock.definition, assert: { op: 'lte', limit: { via: 7, field: 9 } } },
    };
    expect(buildCheck(draftFromCheck(viaCheck), 'stock-move')).toEqual(viaCheck);
    const fieldCheck: ModuleCheck = {
      ...stock, definition: { ...stock.definition, assert: { op: 'lt', limit: { field: 8 } } },
    };
    expect(buildCheck(draftFromCheck(fieldCheck), 'stock-move')).toEqual(fieldCheck);
  });

  it('count needs no field, sum does', () => {
    const d = draftFromCheck(stock);
    d.fieldId = null;
    expect(buildCheck(d, 'm')).toBeUndefined();
    d.function = 'count';
    expect(buildCheck(d, 'm')!.definition).not.toHaveProperty('fieldId');
  });

  it('needs a key, a zh-TW name and message, and the limit that its kind asks for', () => {
    const d = emptyCheckDraft();
    expect(buildCheck(d, 'm')).toBeUndefined();
    Object.assign(d, { key: 'c', name: { 'zh-TW': 'n' }, message: { 'zh-TW': 'm' }, function: 'count' });
    expect(buildCheck(d, 'm')).toBeUndefined(); // constant limit not typed yet
    d.limitConst = 0;
    expect(buildCheck(d, 'm')).toBeDefined();
    d.limitKind = 'via';
    d.limitVia = 3;
    expect(buildCheck(d, 'm')).toBeUndefined(); // target field missing
    d.limitField = 4;
    expect(buildCheck(d, 'm')!.definition.assert.limit).toEqual({ via: 3, field: 4 });
  });

  it('drops an incomplete condition instead of sending it', () => {
    const d = draftFromCheck(stock);
    d.where = { all: [{ field: 0, op: 'eq' }] };
    expect(buildCheck(d, 'm')!.definition).not.toHaveProperty('where');
  });

  it('describes the limit for the list', () => {
    expect(limitText({ const: 0 }, (id) => `#${id}`)).toBe('0');
    expect(limitText({ field: 8 }, (id) => `#${id}`)).toBe('#8');
    expect(limitText({ via: 7, field: 9 }, (id) => `#${id}`)).toBe('#7.#9');
  });

  it('sorts server messages by input', () => {
    const g = groupCheckErrors({
      key: ['代碼已被使用'], 'definition.groupBy[1]': ['不能用小數欄位分組'], 'definition.where': ['不支援欄位對欄位比較'],
      'definition.message': ['缺少訊息'], 'definition.assert.limit': ['上限欄位不是數字'], definition: ['x'],
    });
    expect(g.key).toEqual(['代碼已被使用']);
    expect(g.groupBy).toEqual(['不能用小數欄位分組']);
    expect(g.where).toEqual(['不支援欄位對欄位比較']);
    expect(g.message).toEqual(['缺少訊息']);
    expect(g.assert).toEqual(['上限欄位不是數字']);
    expect(g.general).toEqual(['x']);
  });
});
