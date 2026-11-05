import type { TemplateFieldSummary } from 'src/services/api/dynamic-api';

/** `fields` is every module's fields flattened, with qualified codes (`module.field`) and qualified `dependsOn`. */

/** Unselect `code` and every field that depends on it, directly or indirectly. Locked fields never change. */
export function uncheck(fields: TemplateFieldSummary[], selected: Set<string>, code: string): Set<string> {
  if (fields.find((f) => f.code === code)?.locked) return new Set(selected);
  const next = new Set(selected);
  const drop = (c: string) => {
    if (!next.delete(c)) return;
    for (const f of fields) if (!f.locked && f.dependsOn.includes(c)) drop(f.code);
  };
  drop(code);
  return next;
}

/** Select `code` together with everything it depends on, directly or indirectly. */
export function check(fields: TemplateFieldSummary[], selected: Set<string>, code: string): Set<string> {
  const byCode = new Map(fields.map((f) => [f.code, f]));
  const next = new Set(selected);
  const add = (c: string) => {
    if (next.has(c)) return;
    next.add(c);
    for (const d of byCode.get(c)?.dependsOn ?? []) add(d);
  };
  add(code);
  return next;
}
