import { describe, expect, it } from 'vitest';
import sharedOperators from '../../../../shared/filter-operators.json';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';
import type { FieldType, SchemaField } from 'src/domain/dynamic/types';
import { defaultValueOf, fieldTypeRegistry, operatorsFor } from './field-type-registry';

const ALL: FieldType[] = ['text', 'textarea', 'integer', 'decimal', 'currency', 'percent', 'date', 'datetime', 'time', 'boolean', 'email', 'phone', 'url', 'enum', 'multienum', 'user', 'formula', 'autonumber', 'reference', 'rollup'];

function f(type: FieldType, scale?: number, options: SchemaField['options'] = []): SchemaField {
  return { id: 1, key: 'k', type, label: { 'zh-TW': '欄' }, options, config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true, scale } };
}
const opts = [
  { value: 'opt_1', label: { 'zh-TW': '甲', 'en-US': 'A' }, active: true },
  { value: 'opt_2', label: { 'zh-TW': '乙' }, active: false },
];

describe('fieldTypeRegistry filter operators', () => {
  it('operatorsFor matches every key of the shared fixture', () => {
    const shared = sharedOperators as Record<string, string[]>;
    for (const t of ALL) {
      const field = t === 'formula' || t === 'rollup' ? { ...f(t), config: { ...f(t).config, resultType: 'number' as const } } : f(t);
      expect(operatorsFor(field), t).toEqual(shared[t]);
    }
    const dateFormula = { ...f('formula'), config: { ...f('formula').config, resultType: 'date' as const } };
    expect(operatorsFor(dateFormula)).toEqual(shared['formula:date']);
    expect(Object.keys(shared).sort()).toEqual([...ALL, 'formula:date', 'rollup:date'].sort());
  });

  it('rollup operators follow resultType', () => {
    const shared = sharedOperators as Record<string, string[]>;
    const rollup = (resultType: 'number' | 'currency' | 'date'): SchemaField =>
      ({ ...f('rollup'), config: { ...f('rollup').config, resultType } });
    expect(operatorsFor(rollup('date'))).toEqual(shared['rollup:date']);
    expect(operatorsFor(rollup('currency'))).toEqual(shared.rollup);
    expect(fieldTypeRegistry.rollup.filterInput(rollup('date'), 'gt')?.component).toBe(fieldTypeRegistry.date.component);
  });

  it('filterInput picks components per operator', () => {
    expect(fieldTypeRegistry.date.filterInput(f('date'), 'withinNextDays')?.props).toMatchObject({ scale: 0 });
    expect(fieldTypeRegistry.date.filterInput(f('date'), 'gt')?.props).not.toHaveProperty('withTime');
    expect(fieldTypeRegistry.datetime.filterInput(f('datetime'), 'gt')?.props).toMatchObject({ withTime: true });
    expect(fieldTypeRegistry.text.filterInput(f('text'), 'isEmpty')).toBeUndefined();
    const multi = fieldTypeRegistry.enum.filterInput(f('enum', undefined, opts), 'in');
    expect(multi?.props).toMatchObject({ multiple: true });
    expect((multi?.props.options as { disable: boolean }[]).every((o) => !o.disable)).toBe(true);
    expect(fieldTypeRegistry.enum.filterInput(f('enum', undefined, opts), 'eq')?.props).toMatchObject({ multiple: false });
    expect(fieldTypeRegistry.integer.filterInput(f('integer'), 'between')?.component).toBe(fieldTypeRegistry.integer.component);
    expect(fieldTypeRegistry.boolean.filterInput(f('boolean'), 'eq')?.component).toBe(fieldTypeRegistry.boolean.component);
  });

  it('i18n has every operator label in all four locales', () => {
    const ops = ['eq', 'ne', 'in', 'notIn', 'gt', 'gte', 'lt', 'lte', 'between', 'contains', 'startsWith', 'withinNextDays', 'withinLastDays', 'isEmpty', 'isNotEmpty'];
    for (const loc of [zhTW, enUS, th, vi]) {
      const op = (loc as unknown as { dynamic: { op: Record<string, unknown> } }).dynamic.op;
      for (const k of ops) expect(typeof op[k], k).toBe('string');
      const me = op.multienum as Record<string, string>;
      expect(typeof me.in).toBe('string');
      expect(typeof me.notIn).toBe('string');
    }
    expect(zhTW.dynamic.op.eq).toBe('等於');
    expect(zhTW.dynamic.op.multienum.in).toBe('含任一');
  });
});

describe('fieldTypeRegistry reference', () => {
  const ref = (kind: 'module' | 'entity'): SchemaField => ({
    ...f('reference'), reference: { kind, key: 'x', name: { 'zh-TW': 'X' }, lightUrl: '/modules/x/records/light' },
  });
  const ok = (field: SchemaField, v: unknown) => fieldTypeRegistry.reference.zod(field).safeParse(v).success;

  it('reference zod accepts number for module target and string for entity target', () => {
    expect(ok(ref('module'), 42)).toBe(true);
    expect(ok(ref('entity'), 'M-0001')).toBe(true);
    expect(ok(ref('entity'), true)).toBe(false);
    expect(ok(ref('module'), { a: 1 })).toBe(false);
    expect(ok(ref('module'), null)).toBe(true);
    expect(fieldTypeRegistry.reference.zod({ ...ref('module'), config: { ...f('reference').config, required: true } }).safeParse('').success).toBe(false);
  });

  it('reference filter operators match shared json', () => {
    expect(operatorsFor(ref('module'))).toEqual((sharedOperators as Record<string, string[]>).reference);
  });

  it('reference props and filter inputs use PopupSelect', () => {
    expect(fieldTypeRegistry.reference.props(ref('module'), 'zh-TW')).toMatchObject({ lightUrl: '/modules/x/records/light', valueKind: 'number' });
    expect(fieldTypeRegistry.reference.props(ref('entity'), 'zh-TW')).toMatchObject({ valueKind: 'string' });
    expect(fieldTypeRegistry.reference.filterInput(ref('module'), 'eq')?.props).toMatchObject({ multiple: false });
    expect(fieldTypeRegistry.reference.filterInput(ref('module'), 'notIn')?.props).toMatchObject({ multiple: true });
    expect(fieldTypeRegistry.reference.filterInput(ref('module'), 'isEmpty')).toBeUndefined();
    expect(fieldTypeRegistry.reference.format(42, ref('module'), 'zh-TW')).toBe('42');
  });
});

describe('fieldTypeRegistry', () => {
  it('registry covers all field types', () => {
    expect(Object.keys(fieldTypeRegistry).sort()).toEqual([...ALL].sort());
    for (const t of ALL) {
      const e = fieldTypeRegistry[t];
      expect(e.component).toBeTruthy();
      expect(typeof e.props).toBe('function');
      expect(typeof e.zod).toBe('function');
      expect(typeof e.format).toBe('function');
    }
  });

  it('format renders multienum labels joined by 、 in schema order, unknown raw', () => {
    const field = f('multienum', undefined, opts);
    expect(fieldTypeRegistry.multienum.format(['opt_2', 'opt_1', 'opt_9'], field, 'zh-TW')).toBe('甲、乙、opt_9');
    expect(fieldTypeRegistry.multienum.format(['opt_1'], field, 'en-US')).toBe('A');
  });

  it('formula format follows resultType', () => {
    const fx = (resultType: 'number' | 'currency' | 'date', scale?: number): SchemaField =>
      ({ ...f('formula', scale), config: { ...f('formula', scale).config, resultType } });
    expect(fieldTypeRegistry.formula.format(1234.5, fx('number'), 'zh-TW')).toBe('1234.5');
    expect(fieldTypeRegistry.formula.format(1234.5, fx('currency', 2), 'zh-TW')).toBe('1,234.50');
    expect(fieldTypeRegistry.formula.format('2026-11-03', fx('date'), 'zh-TW')).toBe('2026-11-03');
  });

  it('rollup is read-only and formats by resultType', () => {
    const rx = (resultType: 'number' | 'currency' | 'date', scale?: number): SchemaField =>
      ({ ...f('rollup', scale), config: { ...f('rollup', scale).config, resultType } });
    expect(fieldTypeRegistry.rollup.props(rx('number'), 'zh-TW')).toMatchObject({ readonly: true });
    expect(fieldTypeRegistry.rollup.format(1234567.5, rx('currency', 2), 'zh-TW')).toBe('1,234,567.50');
    expect(fieldTypeRegistry.rollup.format(12, rx('number', 0), 'zh-TW')).toBe('12');
    expect(fieldTypeRegistry.rollup.format('2026-11-03', rx('date'), 'zh-TW')).toBe('2026-11-03');
    expect(fieldTypeRegistry.rollup.zod(rx('number')).safeParse(5).success).toBe(true);
  });

  it('rollup currency null formats empty', () => {
    const cur = (type: 'rollup' | 'formula'): SchemaField => ({ ...f(type, 2), config: { ...f(type, 2).config, resultType: 'currency' } });
    for (const t of ['rollup', 'formula'] as const) {
      expect(fieldTypeRegistry[t].format(null, cur(t), 'zh-TW'), t).toBe('');
      expect(fieldTypeRegistry[t].format(undefined, cur(t), 'zh-TW'), t).toBe('');
    }
  });

  it('formula and autonumber are read-only in forms and accept any stored value', () => {
    for (const t of ['formula', 'autonumber'] as FieldType[]) {
      expect(fieldTypeRegistry[t].props(f(t), 'zh-TW')).toMatchObject({ readonly: true });
      expect(fieldTypeRegistry[t].format('REP-202611-0001', f(t), 'zh-TW')).toBe('REP-202611-0001');
    }
  });

  it('formula filter input follows resultType', () => {
    const date = { ...f('formula'), config: { ...f('formula').config, resultType: 'date' as const } };
    expect(fieldTypeRegistry.formula.filterInput(date, 'gt')?.component).toBe(fieldTypeRegistry.date.component);
    expect(fieldTypeRegistry.formula.filterInput(f('formula', 3), 'gt')?.props).toMatchObject({ scale: 3 });
    expect(fieldTypeRegistry.formula.filterInput(f('formula'), 'isEmpty')).toBeUndefined();
  });

  it('format renders enum label', () => {
    expect(fieldTypeRegistry.enum.format('opt_2', f('enum', undefined, opts), 'zh-TW')).toBe('乙');
  });

  it('format renders currency with thousands separator', () => {
    expect(fieldTypeRegistry.currency.format(1234567.5, f('currency', 2), 'zh-TW')).toBe('1,234,567.50');
    expect(fieldTypeRegistry.currency.format(1234, f('currency'), 'zh-TW')).toBe('1,234.00');
  });

  it('format handles the remaining types', () => {
    expect(fieldTypeRegistry.boolean.format(true, f('boolean'), 'zh-TW')).toBe('是');
    expect(fieldTypeRegistry.boolean.format(false, f('boolean'), 'zh-TW')).toBe('否');
    expect(fieldTypeRegistry.percent.format(12.5, f('percent'), 'zh-TW')).toBe('12.5%');
    expect(fieldTypeRegistry.integer.format(1234567, f('integer'), 'zh-TW')).toBe('1234567');
    expect(fieldTypeRegistry.user.format('sales01', f('user'), 'zh-TW')).toBe('sales01');
    expect(fieldTypeRegistry.datetime.format('2026-11-03T08:05:09+08:00', f('datetime'), 'zh-TW')).toBe('2026-11-03 08:05');
    expect(fieldTypeRegistry.date.format('2026-11-03', f('date'), 'zh-TW')).toBe('2026-11-03');
    for (const t of ALL) expect(fieldTypeRegistry[t].format(null, f(t), 'zh-TW')).toBe('');
  });

  it('props pass label, required and select options', () => {
    const field = { ...f('enum', undefined, opts), config: { required: true, readOnly: true, showInList: true, isFilterable: false, isSortable: false } };
    const p = fieldTypeRegistry.enum.props(field, 'en-US');
    expect(p).toMatchObject({ label: '欄', required: true, readonly: true, multiple: false });
    expect(p.options).toEqual([{ value: 'opt_1', label: 'A', color: undefined, disable: false }, { value: 'opt_2', label: '乙', color: undefined, disable: true }]);
  });
});

describe('defaultValueOf today', () => {
  const dateField = (config: Partial<SchemaField['config']>): SchemaField => ({
    id: 1, key: 'f_1', type: 'date', label: {}, options: [],
    config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false, ...config },
  });
  it('defaultToday gives the injected today', () => {
    expect(defaultValueOf(dateField({ defaultToday: true }), '2026-11-05')).toBe('2026-11-05');
  });
  it('without defaultToday it is the configured default', () => {
    expect(defaultValueOf(dateField({ defaultValue: '2026-01-01' }), '2026-11-05')).toBe('2026-01-01');
  });
  it('defaults to the local date', () => {
    expect(defaultValueOf(dateField({ defaultToday: true }))).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});
