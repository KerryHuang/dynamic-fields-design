import type { ConditionNode, FieldCondition, FilterOperator } from './types';

export type OperatorValueKind = 'none' | 'single' | 'pair' | 'list' | 'days';

export function operatorNeedsValue(op: FilterOperator): OperatorValueKind {
  switch (op) {
    case 'isEmpty':
    case 'isNotEmpty': return 'none';
    case 'between': return 'pair';
    case 'in':
    case 'notIn': return 'list';
    case 'withinNextDays':
    case 'withinLastDays': return 'days';
    default: return 'single';
  }
}

const filled = (v: unknown) => v !== undefined && v !== null && v !== '';

const isFieldRef = (v: unknown): v is { fieldRef: number } =>
  typeof v === 'object' && v !== null && !Array.isArray(v) && 'fieldRef' in v;

function isComplete(c: FieldCondition): boolean {
  if (!c.field) return false;
  const v = c.value;
  switch (operatorNeedsValue(c.op)) {
    case 'none': return true;
    case 'pair': return Array.isArray(v) && v.length === 2 && v.every(filled);
    case 'list': return Array.isArray(v) && v.length > 0;
    case 'days': return typeof v === 'number' && Number.isInteger(v);
    default: return isFieldRef(v) ? v.fieldRef !== 0 : filled(v);
  }
}

/** Drops conditions without a field or a required value, and groups left empty. */
export function pruneCondition(node?: ConditionNode): ConditionNode | undefined {
  if (!node) return undefined;
  if ('all' in node || 'any' in node) {
    const isAll = 'all' in node;
    const children = (isAll ? node.all : node.any)
      .map(pruneCondition)
      .filter((c): c is ConditionNode => c !== undefined);
    if (children.length === 0) return undefined;
    return isAll ? { all: children } : { any: children };
  }
  return isComplete(node) ? node : undefined;
}

export function serializeFilter(node?: ConditionNode): string | undefined {
  const pruned = pruneCondition(node);
  return pruned ? JSON.stringify(pruned) : undefined;
}

export type ConditionGroupNode = Exclude<ConditionNode, FieldCondition>;
export const isConditionGroup = (n: ConditionNode): n is ConditionGroupNode => 'all' in n || 'any' in n;

/** True when the node has a group inside a group, which the single-level editor cannot show. */
export function isNestedCondition(n?: ConditionNode): boolean {
  return !!n && isConditionGroup(n) && ('all' in n ? n.all : n.any).some(isConditionGroup);
}
