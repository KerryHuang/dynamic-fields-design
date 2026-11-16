import { describe, expect, it } from 'vitest';
import {
  buildLedgerBody, newReversalField, numberFields, reversalCandidates, swapPartners, type LedgerDraft,
} from './ledger-draft';
import type { DesignerField, FieldType, ModuleDetail } from './types';

const field = (id: number, type: FieldType, extra: Partial<DesignerField['config']> = {}, status: 'Active' | 'Inactive' = 'Active'): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, blockId: 1, sequence: id, status, options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, ...extra },
});
const mod = (fields: DesignerField[]): ModuleDetail => ({
  id: 1, key: 'journal', programCode: 'P', name: { 'zh-TW': '傳票' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields }],
});

describe('reversalCandidates', () => {
  it('lists read-only reference fields that point at the module itself', () => {
    const self = { refTarget: { kind: 'module' as const, key: 'journal' }, readOnly: true };
    const m = mod([
      field(1, 'reference', self), // yes
      field(2, 'reference', { ...self, readOnly: false }), // writable
      field(3, 'reference', { refTarget: { kind: 'module', key: 'other' }, readOnly: true }), // elsewhere
      field(4, 'reference', { refTarget: { kind: 'entity', key: 'journal' }, readOnly: true }), // an entity, not a module
      field(5, 'text', { readOnly: true }),
      field(6, 'reference', self, 'Inactive'),
    ]);
    expect(reversalCandidates(m).map((f) => f.id)).toEqual([1]);
  });
});

describe('numberFields and swapPartners', () => {
  const fields = [field(1, 'integer'), field(2, 'decimal'), field(3, 'currency'), field(4, 'percent'), field(5, 'text'), field(6, 'decimal', {}, 'Inactive'), field(7, 'decimal')];
  it('number fields are integer, decimal and currency', () => {
    expect(numberFields(fields).map((f) => f.id)).toEqual([1, 2, 3, 7]);
  });
  it('a swap partner has the same type as the first field and is another field', () => {
    expect(swapPartners(fields, 2).map((f) => f.id)).toEqual([7]);
    expect(swapPartners(fields, null)).toEqual([]);
  });
});

describe('buildLedgerBody', () => {
  const draft = (extra: Partial<LedgerDraft> = {}): LedgerDraft => ({ enabled: true, reversalFieldId: 12, negate: [5], swap: [[6, 7], [null, 8], [9, null]], ...extra });
  it('needs the switch on and a reversal field', () => {
    expect(buildLedgerBody(draft({ enabled: false }), false)).toBeUndefined();
    expect(buildLedgerBody(draft({ reversalFieldId: null }), false)).toBeUndefined();
  });
  it('sends only complete swap pairs, and carries the confirmation', () => {
    expect(buildLedgerBody(draft(), true)).toEqual({ reversalFieldId: 12, negate: [5], swap: [[6, 7]], confirmFreeze: true });
    expect(buildLedgerBody(draft(), false)!.confirmFreeze).toBe(false);
  });
});

describe('newReversalField', () => {
  it('is a read-only, optional reference to the module itself', () => {
    expect(newReversalField(3, 'journal', { 'zh-TW': '沖銷自' })).toEqual({
      blockId: 3, type: 'reference', label: { 'zh-TW': '沖銷自' },
      config: { required: false, readOnly: true, showInList: false, isFilterable: false, isSortable: false, refTarget: { kind: 'module', key: 'journal' }, onDelete: 'restrict' },
    });
  });
});
