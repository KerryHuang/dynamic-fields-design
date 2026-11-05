import type { RuleState } from 'src/domain/dynamic/rule-engine';
import type { DetailRowPayload, DynamicRecord, ModuleSchema, ReferenceInfo, SchemaDetail, SchemaField } from 'src/domain/dynamic/types';
import { buildZodSchema } from '../build-zod-schema';
import { defaultValueOf } from '../field-type-registry';
import { ruleStateOf } from '../rule-state';

/** One detail row in the form; kept outside vee-validate (DynamicField binds top-level field keys). */
export type DetailRowState = {
  uid: string;
  id?: number;
  rowVersion?: string;
  values: Record<string, unknown>;
  /** Stored values of an existing row: the change-detection baseline and what ReadOnly rules look at. */
  original?: Record<string, unknown>;
  deleted: boolean;
  /** Reference titles of an existing row, for PopupSelect. */
  references?: Record<string, ReferenceInfo>;
};

let seq = 0;
const nextUid = () => `row${++seq}`;

export function toRowStates(rows: DynamicRecord[] | undefined): DetailRowState[] {
  return (rows ?? []).map((r) => ({
    uid: nextUid(), id: r.id, rowVersion: r.rowVersion, values: { ...r.values }, original: r.values, deleted: false, references: r.references,
  }));
}

/** `headerValues` (by field key) and `headerFields` resolve a field's parentDefaultFieldId to the header's current value. */
export function newRowState(detail: SchemaDetail, headerValues?: Record<string, unknown>, headerFields?: SchemaField[]): DetailRowState {
  const fromHeader = (f: SchemaField) => {
    const id = f.config.parentDefaultFieldId;
    const key = id == null ? undefined : headerFields?.find((h) => h.id === id)?.key;
    return key ? headerValues?.[key] : undefined;
  };
  return {
    uid: nextUid(), deleted: false,
    values: Object.fromEntries(detail.fields.map((f) => [f.key, fromHeader(f) ?? defaultValueOf(f) ?? null])),
  };
}

// undefined and null are the same "no value", like the header form.
const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
const changedKeys = (r: DetailRowState) =>
  [...new Set([...Object.keys(r.values), ...Object.keys(r.original ?? {})])].filter((k) => !same(r.values[k], r.original?.[k]));
/** Rows the server will see: new ones and changed existing ones (deleted rows are not validated). */
const isSent = (r: DetailRowState) => !r.deleted && (r.id === undefined || changedKeys(r).length > 0);

export function buildDetailPayload(rows: DetailRowState[]): { items: DetailRowPayload[]; uidAt: string[] } {
  const items: DetailRowPayload[] = [];
  const uidAt: string[] = [];
  for (const r of rows) {
    let item: DetailRowPayload | undefined;
    if (r.id === undefined) item = r.deleted ? undefined : { values: r.values };
    else if (r.deleted) item = { id: r.id, rowVersion: r.rowVersion!, _delete: true };
    else {
      const changed = changedKeys(r);
      if (changed.length) item = { id: r.id, rowVersion: r.rowVersion!, values: Object.fromEntries(changed.map((k) => [k, r.values[k] ?? null])) };
    }
    if (item) {
      items.push(item);
      uidAt.push(r.uid);
    }
  }
  return { items, uidAt };
}

const KEY = /^details\.([^[\]]+)\[(\d+)\]\.(.+)$/;

/** `details.{childKey}[{i}].{fieldKey}` split up; undefined for any other key (e.g. `details.{childKey}` alone). */
export function parseDetailKey(key: string): { childKey: string; index: number; fieldKey: string } | undefined {
  const m = KEY.exec(key);
  return m ? { childKey: m[1]!, index: Number(m[2]), fieldKey: m[3]! } : undefined;
}

/** Server errors of one detail module as row uid -> field key (`_record` for the row) -> message. */
export function rowErrors(errors: Record<string, string[]> | undefined, childKey: string, uidAt: string[]): Map<string, Record<string, string>> {
  const out = new Map<string, Record<string, string>>();
  for (const [key, messages] of Object.entries(errors ?? {})) {
    const p = parseDetailKey(key);
    const uid = p && p.childKey === childKey ? uidAt[p.index] : undefined;
    if (!uid) continue;
    out.set(uid, { ...out.get(uid), [p!.fieldKey]: messages.join('；') });
  }
  return out;
}

/** The detail as a one-block module schema, for the shared rule and zod helpers. */
function asSchema(detail: SchemaDetail): ModuleSchema {
  return {
    key: detail.moduleKey, programCode: '', name: detail.name, titleFieldKey: detail.titleFieldKey, version: 0,
    permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
    blocks: [{ id: 0, label: {}, collapsed: false, columns: 1, fields: detail.fields }], rules: detail.rules,
  };
}

/** Rule state of one row; ReadOnly rules look at the row's stored values only, like the header form. */
export function rowRuleState(detail: SchemaDetail, row: DetailRowState): RuleState {
  return ruleStateOf(asSchema(detail), row.values, row.original, { skipComputed: true });
}

/** Client checks of the rows that will be sent; row uid -> field key (`_record` for the row) -> first message. */
export function validateRows(detail: SchemaDetail, rows: DetailRowState[], locale = 'zh-TW'): Map<string, Record<string, string>> {
  const schema = asSchema(detail);
  const out = new Map<string, Record<string, string>>();
  for (const r of rows.filter(isSent)) {
    const existing = r.original && ({ values: r.original } as DynamicRecord);
    const storedRuleState = r.original && ruleStateOf(schema, r.original, r.original, { skipComputed: true });
    const result = buildZodSchema(schema, existing ? 'edit' : 'create', existing, rowRuleState(detail, r), { storedRuleState, locale })
      .safeParse(r.values);
    if (result.success) continue;
    const errs: Record<string, string> = {};
    for (const issue of result.error.issues) {
      const key = String(issue.path[0] ?? '_record');
      errs[key] ??= issue.message;
    }
    out.set(r.uid, errs);
  }
  return out;
}
