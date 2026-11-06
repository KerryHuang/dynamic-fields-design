import { describe, expect, it } from 'vitest';
import type { ModuleCatalog, ReportDefinition } from 'src/domain/dynamic/report-types';
import {
  canAdvance, canSave, emptyState, fromDefinition, hasDedupeNote, operatorsOf, setChild, setMainModule, setPrimaryDate, toDefinition, unavailableRefs,
} from './designer-state';

const F = (id: number, label: string, o: Partial<ModuleCatalog['fields'][number]> = {}) => ({
  id, label, type: 'text', canGroup: true, canSum: false, canMinMax: false, isDate: false, options: null, ...o,
});
const CATALOG: ModuleCatalog = {
  fields: [F(1, '客戶', { type: 'reference' }), F(2, '金額', { type: 'currency', canGroup: false, canSum: true, canMinMax: true }), F(3, '訂單日', { type: 'date', isDate: true, canMinMax: true })],
  via: [{ fieldId: 1, label: '客戶', targetKey: 'customers', fields: [F(10, '地區')] }],
  children: [{ moduleKey: 'shipments', name: '出貨', linkFieldId: 20, linkLabel: '訂單', isDetail: false, fields: [F(21, '數量', { canSum: true })], via: [] }],
};
const MAIN = (id: number, via: number | null = null) => ({ source: 'main', fieldId: id, viaFieldId: via });
const DEF: ReportDefinition = {
  version: 1, mainModuleKey: 'orders', child: { moduleKey: 'shipments', linkFieldId: 20 },
  columns: [MAIN(3)], groups: [{ field: MAIN(10, 1), bucket: null }],
  measures: [{ id: 'm1', kind: 'sum', field: MAIN(2), condition: null }],
  filters: [{ field: MAIN(2), op: 'gt', value: 100 }], primaryDateField: MAIN(3), dateRange: { kind: 'thisMonth', n: null },
  sort: [{ key: 'm1', desc: true }], viz: 'bar',
};

describe('designer-state', () => {
  it('toDefinition/fromDefinition round trip', () => {
    expect(toDefinition(fromDefinition(DEF, '月報'))).toEqual(DEF);
  });

  it('cannot advance without main module', () => {
    const s = emptyState();
    expect(canAdvance(s, 0)).toBe(false);
    s.mainModuleKey = 'orders';
    expect(canAdvance(s, 0)).toBe(true);
  });

  it('changing main module clears dependent fields', () => {
    const s = fromDefinition(DEF, 'x');
    setMainModule(s, 'tickets');
    expect(s).toMatchObject({ mainModuleKey: 'tickets', child: null, columns: [], groups: [], measures: [], filters: [], primaryDateField: null, dateRange: null });
  });

  it('changing the child drops only child-sourced settings', () => {
    const s = fromDefinition({ ...DEF, columns: [MAIN(3), { source: 'child', fieldId: 21, viaFieldId: null }],
      measures: [...DEF.measures, { id: 'm2', kind: 'countChild', field: null, condition: null }] }, 'x');
    setChild(s, null);
    expect(s.columns).toEqual([MAIN(3)]);
    expect(s.measures.map((m) => m.id)).toEqual(['m1']);
  });

  it('operators follow the shared table, date formulas use date operators', () => {
    expect(operatorsOf(F(1, 'a', { type: 'boolean' }))).toEqual(['eq', 'isEmpty', 'isNotEmpty']);
    expect(operatorsOf(F(1, 'a', { type: 'formula', isDate: true }))).toContain('withinNextDays');
    expect(operatorsOf(F(1, 'a', { type: 'formula' }))).not.toContain('withinNextDays');
  });

  it('dedupe note appears at group step when merged and main sum', () => {
    expect(hasDedupeNote(fromDefinition(DEF, 'x'))).toBe(true);
    expect(hasDedupeNote(fromDefinition({ ...DEF, child: null }, 'x'))).toBe(false);
    const childOnly = fromDefinition({ ...DEF, measures: [{ id: 'm1', kind: 'sum', field: { source: 'child', fieldId: 21, viaFieldId: null } }] }, 'x');
    expect(hasDedupeNote(childOnly)).toBe(false);
  });

  it('date range needs a primary date field and clearing the field clears the range', () => {
    const s = fromDefinition(DEF, 'x');
    s.primaryDateField = null;
    expect(canAdvance(s, 2)).toBe(false);
    const t = fromDefinition(DEF, 'x');
    setPrimaryDate(t, null);
    expect(t.dateRange).toBeNull();
    expect(canAdvance(t, 2)).toBe(true);
  });

  it('a condition without its value cannot advance, isEmpty needs none', () => {
    const s = fromDefinition({ ...DEF, filters: [{ field: MAIN(2), op: 'gt' }] }, 'x');
    expect(canAdvance(s, 2)).toBe(false);
    s.filters = [{ field: MAIN(2), op: 'isEmpty' }];
    expect(canAdvance(s, 2)).toBe(true);
  });

  it('references missing from the catalog are reported and block saving until removed', () => {
    const s = fromDefinition({ ...DEF, columns: [MAIN(3), MAIN(99)] }, 'x');
    expect(unavailableRefs(s, CATALOG)).toMatchObject({ columns: [1], any: true });
    expect(canSave(s, CATALOG)).toBe(false);
    s.columns.splice(1, 1);
    expect(unavailableRefs(s, CATALOG).any).toBe(false);
    expect(canSave(s, CATALOG)).toBe(true);
  });

  it('a missing child makes its fields unavailable', () => {
    const s = fromDefinition({ ...DEF, child: { moduleKey: 'gone', linkFieldId: 1 }, columns: [{ source: 'child', fieldId: 21, viaFieldId: null }] }, 'x');
    expect(unavailableRefs(s, CATALOG)).toMatchObject({ child: true, columns: [0], any: true });
  });

  it('a definition with masked conditions can never be saved', () => {
    const s = fromDefinition({ ...DEF, filters: [{ field: MAIN(2), op: 'gt', hidden: true }] }, 'x');
    expect(s.hasHidden).toBe(true);
    expect(canSave(s, CATALOG)).toBe(false);
    const m = fromDefinition({ ...DEF, measures: [{ id: 'm1', kind: 'ratio', field: null, condition: { field: MAIN(2), op: 'gt', hidden: true } }] }, 'x');
    expect(m.hasHidden).toBe(true);
  });
});
