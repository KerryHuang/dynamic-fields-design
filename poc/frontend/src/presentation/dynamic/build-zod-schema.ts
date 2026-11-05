import { z, type ZodTypeAny } from 'zod';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { RuleState } from 'src/domain/dynamic/rule-engine';
import type { DynamicRecord, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import { zodFor } from './field-type-registry';
import { isWritable } from './rule-state';

const OUT_OF_RANGE = '選項不在可選範圍';

export interface RuleOptions {
  /** Rule state computed from the stored record's values; lets the option check tell which options are new. */
  storedRuleState?: RuleState;
  locale?: string;
}

const sameSet = (a: Set<string> | undefined, b: Set<string> | undefined) =>
  a === b || (!!a && !!b && a.size === b.size && [...a].every((x) => b.has(x)));

/**
 * Keys are Field.Key. In edit mode, the record's stored select values stay valid even if the option is now inactive.
 * With a RuleState, required comes from the rules (hidden and unwritable fields are not required-checked) and
 * failed Validation and OptionFilter rules are reported as issues.
 */
export function buildZodSchema(
  schema: ModuleSchema,
  mode: 'create' | 'edit',
  existing?: DynamicRecord,
  ruleState?: RuleState,
  { storedRuleState, locale = 'zh-TW' }: RuleOptions = {},
) {
  const shape: Record<string, ZodTypeAny> = {};
  const fields: SchemaField[] = schema.blocks.flatMap((b) => b.fields);
  for (const field of fields) {
    const required = ruleState ? ruleState.required.has(field.id) && isWritable(field, ruleState) : field.config.required;
    shape[field.key] = zodFor(field, mode === 'edit' ? existing?.values[field.key] : undefined, required);
  }
  return z.object(shape).superRefine((data, ctx) => {
    if (!ruleState) return;
    const stored = mode === 'edit' ? existing?.values : undefined;

    // Option range (M2): only fields the user can write. Old values stay valid unless the allowed set moved.
    for (const field of fields) {
      const allowed = ruleState.allowedOptions.get(field.id);
      const value = data[field.key];
      if (!allowed || (typeof value !== 'string' && !Array.isArray(value)) || !isWritable(field, ruleState)) continue;
      const chosen = (Array.isArray(value) ? value : [value]) as string[];
      const previous = stored ? [stored[field.key]].flat() : [];
      const recheckAll = !stored || !sameSet(allowed, storedRuleState?.allowedOptions.get(field.id));
      const bad = chosen.some((o) => !allowed.has(o) && (recheckAll || !previous.includes(o)));
      if (bad) ctx.addIssue({ code: 'custom', message: OUT_OF_RANGE, path: [field.key] });
    }

    // Validation (M3): first target that is not rule-hidden (the schema holds only fields visible to the user), else _record.
    const keyOf = new Map(fields.map((f) => [f.id, f.key]));
    for (const failed of ruleState.failedValidations) {
      const target = failed.targets.find((id) => keyOf.has(id) && !ruleState.hidden.has(id));
      ctx.addIssue({
        code: 'custom', message: resolveI18n(failed.message, locale),
        path: [target === undefined ? '_record' : keyOf.get(target)!],
      });
    }
  });
}
