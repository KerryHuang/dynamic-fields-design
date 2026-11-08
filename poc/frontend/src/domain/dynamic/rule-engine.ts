import sharedOperators from '../../../../shared/filter-operators.json';
import type {
  ConditionNode, FieldCondition, FieldConfig, FilterOperator, I18nText, SchemaField, SchemaRule,
} from './types';

/** The engine only needs these properties; shared rule cases carry nothing more. */
export type SchemaFieldLike = Pick<SchemaField, 'id' | 'key' | 'type'> & { config?: Partial<FieldConfig> };

export interface RuleState {
  hidden: Set<number>;
  required: Set<number>;
  readOnly: Set<number>;
  readOnlyAll: boolean;
  /** Field.Id -> opt_<id> values; a key with an empty set means the filters do not overlap. */
  allowedOptions: Map<number, Set<string>>;
  failedValidations: { targets: number[]; message?: I18nText }[];
}

// Semantics mirror the backend ConditionEvaluator / RuleEngine; poc/shared/rule-cases.json is the single truth.

type Kind = 'number' | 'date' | 'text';
interface IndexValue { kind: Kind; value: number | string }

const DAY_MS = 86_400_000;
const BUSINESS_OFFSET_MS = 8 * 3_600_000; // +08:00

const OPERATORS_BY_TYPE = sharedOperators as Record<string, FilterOperator[]>;
const EMPTY_OPS: FilterOperator[] = ['isEmpty', 'isNotEmpty'];
export const REF_OPS: FilterOperator[] = ['eq', 'ne', 'gt', 'gte', 'lt', 'lte'];

/** Shared filter table, except textarea: rules allow the empty operators there (decision 7). */
export function operatorsFor(f: SchemaFieldLike): FilterOperator[] {
  if (f.type === 'textarea') return EMPTY_OPS;
  const key = (f.type === 'formula' || f.type === 'rollup') && f.config?.resultType === 'date' ? `${f.type}:date` : f.type;
  return OPERATORS_BY_TYPE[key] ?? [];
}

/** Kind that a field's index values have; null when the field cannot be compared with fieldRef. */
export function refKindOf(f: SchemaFieldLike): Kind | null {
  switch (f.type) {
    case 'integer': case 'decimal': case 'currency': case 'percent': case 'boolean': return 'number';
    case 'formula': case 'rollup': return f.config?.resultType === 'date' ? 'date' : 'number';
    case 'date': case 'datetime': return 'date';
    case 'text': case 'email': case 'phone': case 'url': case 'time': case 'autonumber': return 'text';
    default: return null;
  }
}

const isGroup = (n: ConditionNode): n is { all: ConditionNode[] } | { any: ConditionNode[] } =>
  'all' in n || 'any' in n;
const childrenOf = (n: { all: ConditionNode[] } | { any: ConditionNode[] }) => ('all' in n ? n.all : n.any);

/** Drops empty groups the way the backend parser does; undefined means "no condition". */
function prune(node: ConditionNode | undefined): ConditionNode | undefined {
  if (!node) return undefined;
  if (!isGroup(node)) return node;
  const kids = childrenOf(node).map(prune).filter((c): c is ConditionNode => c !== undefined);
  if (kids.length === 0) return undefined;
  return 'all' in node ? { all: kids } : { any: kids };
}

function refFieldId(c: FieldCondition): number | undefined {
  const v = c.value;
  return v !== null && typeof v === 'object' && !Array.isArray(v) && 'fieldRef' in v
    ? Number((v as { fieldRef: unknown }).fieldRef)
    : undefined;
}

/** False when a rule's condition could not have been parsed by the backend (unknown field, bad operator, bad fieldRef). */
export function isEvaluable(node: ConditionNode | undefined, fields: Map<number, SchemaFieldLike>): boolean {
  if (!node) return true;
  if (isGroup(node)) return childrenOf(node).every((c) => isEvaluable(c, fields));
  const field = fields.get(node.field);
  if (!field || !operatorsFor(field).includes(node.op)) return false;
  const refId = refFieldId(node);
  if (refId === undefined) return true;
  const ref = fields.get(refId);
  if (!ref || !REF_OPS.includes(node.op)) return false;
  const kind = refKindOf(field);
  return kind !== null && kind === refKindOf(ref);
}

const dateToMs = (s: string) => Date.parse(`${s}T00:00:00Z`);
// datetime shifted into +08:00 so its UTC fields read as business-timezone wall clock
const dateTimeToMs = (s: string) => Date.parse(s) + BUSINESS_OFFSET_MS;

function indexOne(f: SchemaFieldLike, v: unknown): IndexValue | null {
  if (v === null || v === undefined) return null;
  switch (f.type) {
    case 'integer': case 'decimal': case 'currency': case 'percent':
      return { kind: 'number', value: Number(v) };
    case 'boolean': return { kind: 'number', value: v ? 1 : 0 };
    case 'formula': case 'rollup':
      return f.config?.resultType === 'date'
        ? { kind: 'date', value: dateToMs(String(v)) }
        : { kind: 'number', value: Number(v) };
    case 'date': return { kind: 'date', value: dateToMs(String(v)) };
    case 'datetime': return { kind: 'date', value: dateTimeToMs(String(v)) };
    default: return { kind: 'text', value: String(v) };
  }
}

// backend ConditionParser normalizes text / textarea operands with Trim; keep the same
const operandIndexOne = (f: SchemaFieldLike, v: unknown) =>
  (f.type === 'text' || f.type === 'textarea') && typeof v === 'string' ? indexOne(f, v.trim()) : indexOne(f, v);

/** Empty (missing, null, empty array) gives []; textarea only distinguishes empty string from non-empty. */
function indexOf(f: SchemaFieldLike, values: Record<string, unknown>): IndexValue[] {
  const v = values[f.key];
  if (v === null || v === undefined) return [];
  if (f.type === 'textarea') return typeof v === 'string' && v.length > 0 ? [{ kind: 'text', value: v }] : [];
  if (f.type === 'multienum') return (v as unknown[]).map((x) => ({ kind: 'text', value: String(x) }));
  const one = indexOne(f, v);
  return one ? [one] : [];
}

/** null when the two sides are not comparable; ordinal (code unit) order for text. */
function compareOrNull(a: IndexValue, b: IndexValue): number | null {
  if (a.kind !== b.kind) return null;
  if (Number.isNaN(a.value) || Number.isNaN(b.value)) return null;
  return a.value < b.value ? -1 : a.value > b.value ? 1 : 0;
}

// not comparable: every ordering and eq fail, ne holds (the parser prevents this for valid rules)
const cmpHolds = (a: IndexValue, b: IndexValue, test: (c: number) => boolean) => {
  const c = compareOrNull(a, b);
  return c === null ? false : test(c);
};

function operandsOf(c: FieldCondition, field: SchemaFieldLike): IndexValue[] {
  const elementField: SchemaFieldLike = field.type === 'multienum' ? { ...field, type: 'enum' } : field;
  const raw = Array.isArray(c.value) ? c.value : [c.value];
  return raw.map((x) => operandIndexOne(elementField, x)).filter((x): x is IndexValue => x !== null);
}

function withinDays(v: IndexValue, from: number, to: number): boolean {
  if (v.kind !== 'date') return false;
  const day = Math.floor((v.value as number) / DAY_MS);
  return day >= from && day <= to;
}

function evaluateField(
  c: FieldCondition, values: Record<string, unknown>, fields: Map<number, SchemaFieldLike>, today: string,
): boolean {
  const field = fields.get(c.field);
  if (!field) throw new Error(`Unknown field ${c.field} in condition`);
  const left = indexOf(field, values);
  if (c.op === 'isEmpty') return left.length === 0;
  if (c.op === 'isNotEmpty') return left.length > 0;
  if (left.length === 0) return c.op === 'ne' || c.op === 'notIn';

  const refId = refFieldId(c);
  let operands: IndexValue[];
  if (refId !== undefined) {
    const right = indexOf(fields.get(refId)!, values);
    if (right.length === 0) return c.op === 'ne';
    operands = [right[0]];
  } else if (c.op === 'contains' || c.op === 'startsWith') {
    operands = [{ kind: 'text', value: String(c.value) }];
  } else {
    operands = operandsOf(c, field);
  }

  const l = left[0];
  const todayDay = Math.floor(dateToMs(today) / DAY_MS);
  switch (c.op) {
    case 'eq': return cmpHolds(l, operands[0], (c) => c === 0);
    case 'ne': return !cmpHolds(l, operands[0], (c) => c === 0);
    case 'in': return left.some((x) => operands.some((o) => cmpHolds(x, o, (c) => c === 0)));
    case 'notIn': return !left.some((x) => operands.some((o) => cmpHolds(x, o, (c) => c === 0)));
    case 'gt': return cmpHolds(l, operands[0], (c) => c > 0);
    case 'gte': return cmpHolds(l, operands[0], (c) => c >= 0);
    case 'lt': return cmpHolds(l, operands[0], (c) => c < 0);
    case 'lte': return cmpHolds(l, operands[0], (c) => c <= 0);
    case 'between':
      return cmpHolds(l, operands[0], (c) => c >= 0) && cmpHolds(l, operands[1], (c) => c <= 0);
    case 'contains': return String(l.value).includes(String(operands[0].value));
    case 'startsWith': return String(l.value).startsWith(String(operands[0].value));
    case 'withinNextDays': return withinDays(l, todayDay, todayDay + Number(c.value));
    case 'withinLastDays': return withinDays(l, todayDay - Number(c.value), todayDay);
    case 'beforeToday': return l.kind === 'date' && Math.floor((l.value as number) / DAY_MS) < todayDay;
    default: throw new Error(`Unsupported operator ${String(c.op)}`);
  }
}

/**
 * Evaluates a condition against record values (keys are Field.Key).
 * Empty condition (undefined or empty groups) holds. Left side empty: only isEmpty, ne, notIn hold;
 * fieldRef counterpart empty: only ne holds. Throws on a condition that isEvaluable rejects.
 */
export function evaluateCondition(
  node: ConditionNode | undefined, values: Record<string, unknown>, fields: Map<number, SchemaFieldLike>, today: string,
): boolean {
  const n = prune(node);
  if (!n) return true;
  if (isGroup(n)) {
    const results = childrenOf(n).map((c) => evaluateCondition(c, values, fields, today));
    return 'all' in n ? results.every(Boolean) : results.some(Boolean);
  }
  return evaluateField(n, values, fields, today);
}

const canonicalId = (s: string) => {
  const m = /^[1-9]\d*$/.exec(s);
  return m ? Number(s) : null;
};

/** Targets expanded to field ids in target order; blocks become their fields, unknown ids and `*` produce nothing. */
function expandTargets(
  targets: string[], fields: Map<number, SchemaFieldLike>, blocks: { id: number; fieldIds: number[] }[],
): number[] {
  const seen = new Set<number>();
  const out: number[] = [];
  for (const t of targets) {
    let ids: number[] = [];
    if (t.startsWith('field:')) {
      const id = canonicalId(t.slice(6));
      if (id !== null) ids = [id];
    } else if (t.startsWith('block:')) {
      const id = canonicalId(t.slice(6));
      if (id !== null) ids = blocks.find((b) => b.id === id)?.fieldIds ?? [];
    }
    for (const id of ids) {
      if (fields.has(id) && !seen.has(id)) {
        seen.add(id);
        out.push(id);
      }
    }
  }
  return out;
}

/**
 * Evaluates the module's rules for one record. `current` is the value set being edited, `stored` the loaded
 * record (undefined when creating). Visibility needs all rules to hold; Required and ReadOnly need any;
 * OptionFilter intersects; Validation is checked per rule. ReadOnly uses `stored` only, so it never holds on create.
 * Rules whose condition references unknown fields or invalid operators are skipped.
 */
export function evaluateRules(
  rules: SchemaRule[],
  current: Record<string, unknown>,
  stored: Record<string, unknown> | undefined,
  fields: SchemaFieldLike[],
  blocks: { id: number; fieldIds: number[] }[],
  today: string,
): RuleState {
  const byId = new Map(fields.map((f) => [f.id, f]));
  const state: RuleState = {
    hidden: new Set(),
    required: new Set(fields.filter((f) => f.config?.required).map((f) => f.id)),
    readOnly: new Set(),
    readOnlyAll: false,
    allowedOptions: new Map(),
    failedValidations: [],
  };

  for (const rule of rules) {
    if (!isEvaluable(rule.condition, byId)) continue;
    const values = rule.kind === 'readOnly' ? stored : current;
    if (!values) continue;
    const holds = evaluateCondition(rule.condition, values, byId, today);
    const targets = () => expandTargets(rule.targets, byId, blocks);

    if (rule.kind === 'visibility' && !holds) targets().forEach((id) => state.hidden.add(id));
    else if (rule.kind === 'required' && holds) targets().forEach((id) => state.required.add(id));
    else if (rule.kind === 'readOnly' && holds) {
      targets().forEach((id) => state.readOnly.add(id));
      if (rule.targets.includes('*')) state.readOnlyAll = true;
    } else if (rule.kind === 'optionFilter' && holds) {
      for (const id of targets()) {
        const prev = state.allowedOptions.get(id);
        const ids = rule.options;
        state.allowedOptions.set(id, new Set(prev ? ids.filter((o) => prev.has(o)) : ids));
      }
    } else if (rule.kind === 'validation' && !holds) {
      state.failedValidations.push({ targets: targets(), message: rule.message });
    }
  }

  state.hidden.forEach((id) => state.required.delete(id));
  return state;
}
