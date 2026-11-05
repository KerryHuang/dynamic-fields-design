import { evaluateRules, type RuleState } from 'src/domain/dynamic/rule-engine';
import type { ConditionNode, ModuleSchema } from 'src/domain/dynamic/types';
import { normalizeForRules } from './field-type-registry';

/** Local yyyy-MM-dd of the machine (no existing date utility); the backend uses business time (+08:00). */
export function todayLocal(now = new Date()): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${p(now.getMonth() + 1)}-${p(now.getDate())}`;
}

const COMPUTED = new Set(['formula', 'autonumber', 'rollup']);

function referencedIds(node: ConditionNode, out: number[] = []): number[] {
  if ('all' in node) node.all.forEach((c) => referencedIds(c, out));
  else if ('any' in node) node.any.forEach((c) => referencedIds(c, out));
  else {
    out.push(node.field);
    const v = node.value;
    if (v !== null && typeof v === 'object' && !Array.isArray(v) && 'fieldRef' in v) out.push(Number((v as { fieldRef: unknown }).fieldRef));
  }
  return out;
}

/**
 * Rule state for a form model or stored record; `stored` is undefined on create (ReadOnly never holds then).
 * With `skipComputed` (the form), rules whose condition references a formula, autonumber or rollup field are not evaluated:
 * the client never computes those values, so the server judges such rules on save.
 */
export function ruleStateOf(
  schema: ModuleSchema, current: Record<string, unknown>, stored: Record<string, unknown> | undefined,
  { skipComputed = false } = {},
): RuleState {
  const fields = schema.blocks.flatMap((b) => b.fields);
  const computedIds = new Set(fields.filter((f) => COMPUTED.has(f.type)).map((f) => f.id));
  const rules = skipComputed
    ? schema.rules.filter((r) => !referencedIds(r.condition).some((id) => computedIds.has(id)))
    : schema.rules;
  const normalized = (values: Record<string, unknown>) =>
    Object.fromEntries(fields.map((f) => [f.key, normalizeForRules(values[f.key], f.type)]));
  return evaluateRules(
    rules, normalized(current), stored && normalized(stored), fields,
    schema.blocks.map((b) => ({ id: b.id, fieldIds: b.fields.map((f) => f.id) })), todayLocal(),
  );
}

/** Field the user can type into; permission read-only is already folded into `config.readOnly` by the backend schema (GetModuleSchemaHandler). */
export function isWritable(f: { id: number; type: string; config: { readOnly: boolean } }, state: RuleState): boolean {
  return !state.readOnlyAll && !state.readOnly.has(f.id) && !f.config.readOnly && !COMPUTED.has(f.type);
}
