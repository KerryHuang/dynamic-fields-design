import { pruneCondition } from './condition';
import type { CheckLimit, CheckOp, ModuleCheck } from './events-ledger';
import type { ConditionNode, I18nText } from './types';

export type LimitKind = 'const' | 'field' | 'via';

export interface CheckDraft {
  key: string;
  name: I18nText;
  active: boolean;
  groupBy: number[];
  function: 'sum' | 'count';
  fieldId: number | null;
  where?: ConditionNode;
  op: CheckOp;
  limitKind: LimitKind;
  limitConst: number | null;
  /** `field` kind: a field of this module. `via` kind: a field of the module the reference points at. */
  limitField: number | null;
  limitVia: number | null;
  message: I18nText;
}

export const emptyCheckDraft = (): CheckDraft => ({
  key: '', name: {}, active: true, groupBy: [], function: 'sum', fieldId: null, op: 'gte',
  limitKind: 'const', limitConst: null, limitField: null, limitVia: null, message: {},
});

export function draftFromCheck(c: ModuleCheck): CheckDraft {
  const d = c.definition;
  const l = d.assert.limit as { const?: number; field?: number; via?: number };
  const kind: LimitKind = l.via != null ? 'via' : l.field != null ? 'field' : 'const';
  return {
    key: c.key, name: { ...c.name }, active: c.active, groupBy: [...d.groupBy], function: d.function, fieldId: d.fieldId ?? null,
    ...(d.where && { where: JSON.parse(JSON.stringify(d.where)) as ConditionNode }), op: d.assert.op, limitKind: kind,
    limitConst: l.const ?? null, limitField: l.field ?? null, limitVia: l.via ?? null, message: { ...d.message },
  };
}

function buildLimit(d: CheckDraft): CheckLimit | undefined {
  if (d.limitKind === 'const') return d.limitConst == null || !Number.isFinite(d.limitConst) ? undefined : { const: d.limitConst };
  if (d.limitKind === 'field') return d.limitField == null ? undefined : { field: d.limitField };
  return d.limitVia == null || d.limitField == null ? undefined : { via: d.limitVia, field: d.limitField };
}

/** Request body, or undefined while a required pick is missing (the server validates the rest and its messages are shown as they come). */
export function buildCheck(d: CheckDraft, moduleKey: string): ModuleCheck | undefined {
  if (!d.key.trim() || !d.name['zh-TW']?.trim() || !d.message['zh-TW']?.trim()) return undefined;
  if (d.function === 'sum' && d.fieldId == null) return undefined;
  const limit = buildLimit(d);
  if (!limit) return undefined;
  const where = pruneCondition(d.where);
  return {
    key: d.key.trim(), name: d.name, moduleKey, active: d.active,
    definition: {
      groupBy: [...d.groupBy], function: d.function, ...(d.function === 'sum' && { fieldId: d.fieldId! }), ...(where && { where }),
      assert: { op: d.op, limit }, message: d.message,
    },
  };
}

/** `label` names a field Id; a constant is shown as is. */
export function limitText(limit: CheckLimit, label: (fieldId: number) => string): string {
  const l = limit as { const?: number; field?: number; via?: number };
  if (l.via != null) return `${label(l.via)}.${label(l.field!)}`;
  if (l.field != null) return label(l.field);
  return String(l.const);
}

export interface CheckErrors {
  key: string[];
  name: string[];
  groupBy: string[];
  field: string[];
  where: string[];
  assert: string[];
  message: string[];
  general: string[];
}

/** Sorts 400 `errors` (keys like `definition.groupBy[1]`) by the input they belong to. */
export function groupCheckErrors(errors: Record<string, string[]> | undefined): CheckErrors {
  const out: CheckErrors = { key: [], name: [], groupBy: [], field: [], where: [], assert: [], message: [], general: [] };
  const rules: [RegExp, keyof CheckErrors][] = [
    [/^key/i, 'key'], [/^name/i, 'name'], [/^definition\.groupBy/i, 'groupBy'], [/^definition\.fieldId/i, 'field'],
    [/^definition\.where/i, 'where'], [/^definition\.assert/i, 'assert'], [/^definition\.message/i, 'message'],
  ];
  for (const [k, messages] of Object.entries(errors ?? {})) {
    out[rules.find(([re]) => re.test(k))?.[1] ?? 'general'].push(...messages);
  }
  return out;
}
