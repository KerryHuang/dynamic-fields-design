import { describe, expect, it } from 'vitest';
import cases from '../../../../shared/rule-cases.json';
import type { ConditionNode, FieldConfig, SchemaRule } from './types';
import { evaluateCondition, evaluateRules, type SchemaFieldLike } from './rule-engine';

interface CaseField { id: number; key: string; type: SchemaFieldLike['type']; active?: boolean; config?: Partial<FieldConfig> }
interface CaseBlock { id: number; fieldIds: number[] }
interface CaseRule {
  kind: SchemaRule['kind']; condition: ConditionNode; targets: string[];
  optionIds?: number[]; message?: SchemaRule['message']; active?: boolean;
}
interface Expected {
  hidden: number[]; required: number[]; readOnly: number[]; readOnlyAll: boolean;
  allowedOptions: Record<string, number[]>; failedValidations: number[][];
}
interface OutcomeCase {
  name: string; rules: CaseRule[]; values: Record<string, unknown>; stored: Record<string, unknown> | null;
  fields?: CaseField[]; blocks?: CaseBlock[]; expected: Expected;
}

const { today } = cases as { today: string };
const activeFields = (fields: CaseField[]): SchemaFieldLike[] =>
  fields.filter((f) => f.active !== false).map(({ id, key, type, config }) => ({ id, key, type, config }));
const sorted = (s: Iterable<number>) => [...s].sort((a, b) => a - b);

describe('rule engine: shared conditions', () => {
  const fields = new Map(activeFields(cases.fields as CaseField[]).map((f) => [f.id, f]));
  it.each((cases.conditions as unknown as { name: string; condition: ConditionNode; values: Record<string, unknown>; expected: boolean }[])
    .map((c, i) => [i, c.name, c] as const))('#%i %s', (_i, _name, c) => {
    expect(evaluateCondition(c.condition, c.values, fields, today)).toBe(c.expected);
  });
});

describe('rule engine: shared outcomes', () => {
  it.each((cases.outcomes as unknown as OutcomeCase[]).map((o, i) => [i, o.name, o] as const))('#%i %s', (_i, _name, o) => {
    const fields = activeFields(o.fields ?? (cases.fields as CaseField[]));
    const blocks = o.blocks ?? (cases.blocks as CaseBlock[]);
    const rules: SchemaRule[] = o.rules
      .filter((r) => r.active !== false)
      .map((r) => ({
        kind: r.kind, condition: r.condition, targets: r.targets,
        options: (r.optionIds ?? []).map((id) => `opt_${id}`), message: r.message,
      }));

    const state = evaluateRules(rules, o.values, o.stored ?? undefined, fields, blocks, today);

    const e = o.expected;
    expect(sorted(state.hidden)).toEqual(sorted(e.hidden));
    expect(sorted(state.required)).toEqual(sorted(e.required));
    expect(sorted(state.readOnly)).toEqual(sorted(e.readOnly));
    expect(state.readOnlyAll).toBe(e.readOnlyAll);
    const allowed = Object.fromEntries([...state.allowedOptions].map(([id, s]) => [String(id), [...s].sort()]));
    const expectedAllowed = Object.fromEntries(
      Object.entries(e.allowedOptions).map(([id, ids]) => [id, ids.map((x) => `opt_${x}`).sort()]),
    );
    expect(allowed).toEqual(expectedAllowed);
    expect(state.failedValidations.map((v) => v.targets)).toEqual(e.failedValidations);
  });
});

describe('rule engine: skipped and degenerate rules', () => {
  const fields: SchemaFieldLike[] = [
    { id: 1, key: 'n', type: 'integer' },
    { id: 2, key: 'd', type: 'date' },
    { id: 3, key: 't', type: 'text' },
    { id: 4, key: 'dt', type: 'datetime' },
  ];
  const hide = (condition: ConditionNode): SchemaRule => ({ kind: 'visibility', condition, targets: ['field:3'], options: [] });
  const run = (rule: SchemaRule, values: Record<string, unknown>) =>
    evaluateRules([rule], values, undefined, fields, [], today);

  it.each([
    ['unknown field', { field: 99, op: 'eq', value: 1 }],
    ['operator not allowed for type', { field: 1, op: 'contains', value: '1' }],
    ['fieldRef with mismatched kind', { field: 1, op: 'eq', value: { fieldRef: 3 } }],
  ] as [string, ConditionNode][])('skips rule: %s', (_n, cond) => {
    // a hold-nothing condition would hide field 3 if the rule were evaluated
    expect(run(hide(cond), { n: 5, t: 'x' }).hidden.size).toBe(0);
  });

  it('NaN operand is not comparable (eq fails, ne holds)', () => {
    const eq = run(hide({ field: 1, op: 'eq', value: 'abc' }), { n: Number.NaN }); // eq false -> hidden
    expect(eq.hidden.has(3)).toBe(true);
    const ne = run(hide({ field: 1, op: 'ne', value: 'abc' }), { n: Number.NaN }); // ne true -> shown
    expect(ne.hidden.has(3)).toBe(false);
  });
});
