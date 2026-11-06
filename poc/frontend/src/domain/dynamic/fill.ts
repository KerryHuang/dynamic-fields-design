import type { FillConfig, FillMap } from './types';

export const fieldKeyOf = (id: number) => `f_${id}`;

const isEmpty = (v: unknown) => v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0);
const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

/**
 * Values to put into the form after the reference changed (a preview; the server recomputes on save).
 * `source` is the picked record's values (undefined when the reference was cleared), `current` the form's values,
 * `baseline` the stored values in edit mode: a default-mode field still holding its stored value counts as untouched.
 * lock always follows the source (a missing or hidden source field clears it); default only fills a blank or untouched, non-empty source.
 */
export function fillPatch(
  fill: FillConfig, source: Record<string, unknown> | undefined,
  current: Record<string, unknown>, baseline?: Record<string, unknown>,
): Record<string, unknown> {
  const patch: Record<string, unknown> = {};
  for (const { from, to } of fill.map) {
    const key = fieldKeyOf(to);
    const value = source?.[fieldKeyOf(from)] ?? null;
    if (fill.mode === 'lock') {
      patch[key] = value;
    } else if (!isEmpty(value) && (isEmpty(current[key]) || (baseline !== undefined && same(current[key], baseline[key])))) {
      patch[key] = value;
    }
  }
  return patch;
}

/** Titles of copied reference values: the source record's reference info, re-keyed to the target field, for the keys that were filled. */
export function fillReferenceInfos<T>(fill: FillConfig, patch: Record<string, unknown>, sourceReferences: Record<string, T> | undefined): Record<string, T> {
  return Object.fromEntries(fill.map.flatMap(({ from, to }) => {
    const info = sourceReferences?.[fieldKeyOf(from)];
    return fieldKeyOf(to) in patch && info ? [[fieldKeyOf(to), info]] : [];
  }));
}

/** Keys of the fields a lock-mode fill writes: the form shows them read-only even if the field config forgot to. */
export function lockedFillKeys(fields: { config: { fill?: FillConfig } }[]): Set<string> {
  return new Set(fields.flatMap((f) => (f.config.fill?.mode === 'lock' ? f.config.fill.map.map((m) => fieldKeyOf(m.to)) : [])));
}

/** Rows of the editor with both sides picked, as the saved map. */
export const completeMap = (rows: { from: number | null; to: number | null }[]): FillMap[] =>
  rows.flatMap((r) => (r.from == null || r.to == null ? [] : [{ from: r.from, to: r.to }]));
