import { describe, expect, it } from 'vitest';
import type { TemplateFieldSummary } from 'src/services/api/dynamic-api';
import { check, uncheck } from './template-selection';

const f = (code: string, dependsOn: string[] = [], locked = false): TemplateFieldSummary =>
  ({ code, label: {}, type: 'text', locked, dependsOn });
const fields = [
  f('order.title', [], true),
  f('order.subtotal'),
  f('order.taxRate'),
  f('order.tax', ['order.subtotal', 'order.taxRate']),
  f('order.total', ['order.subtotal', 'order.tax']),
];
const all = () => new Set(fields.map((x) => x.code));

describe('template-selection', () => {
  it('uncheck removes the field and everything that depends on it, directly or not', () => {
    const next = uncheck(fields, all(), 'order.taxRate');
    expect([...next].sort()).toEqual(['order.subtotal', 'order.title']);
  });

  it('check brings back the dependencies of the field', () => {
    const after = uncheck(fields, all(), 'order.taxRate');
    const next = check(fields, after, 'order.total');
    expect([...next].sort()).toEqual(['order.subtotal', 'order.tax', 'order.taxRate', 'order.title', 'order.total']);
  });

  it('uncheck leaves locked fields alone and does not mutate its input', () => {
    const sel = all();
    expect(uncheck(fields, sel, 'order.title')).toEqual(sel);
    uncheck(fields, sel, 'order.tax');
    expect(sel.size).toBe(5);
  });
});
