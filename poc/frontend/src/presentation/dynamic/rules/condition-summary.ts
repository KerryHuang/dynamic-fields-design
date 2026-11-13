import { isConditionGroup } from 'src/domain/dynamic/condition';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, SchemaField } from 'src/domain/dynamic/types';

type Translate = (key: string) => string;

/** One-line reading of a condition tree (rules tab rows, approval flow cards); null or an empty group reads "always". */
export function conditionSummary(n: ConditionNode | null | undefined, fields: SchemaField[], t: Translate, locale: string): string {
  if (!n) return t('designer.rules.always');
  const fieldById = (id: number) => fields.find((f) => f.id === id);
  const fieldLabel = (id: number) => { const f = fieldById(id); return f ? resolveI18n(f.label, locale) : `#${id}`; };
  const valueText = (f: SchemaField | undefined, v: unknown): string => {
    if (typeof v === 'object' && v !== null && !Array.isArray(v) && 'fieldRef' in v) return fieldLabel((v as { fieldRef: number }).fieldRef);
    if (Array.isArray(v)) return v.map((x) => valueText(f, x)).join(', ');
    const opt = f?.options.find((o) => o.value === v);
    return opt ? resolveI18n(opt.label, locale) : String(v);
  };
  const summary = (node: ConditionNode): string => {
    if (isConditionGroup(node)) {
      const kids = 'all' in node ? node.all : node.any;
      const joined = kids.map((c) => (isConditionGroup(c) ? `(${summary(c)})` : summary(c)))
        .join(` ${t('all' in node ? 'designer.rules.and' : 'designer.rules.or')} `);
      return joined || t('designer.rules.always');
    }
    const f = fieldById(node.field);
    const op = t(f?.type === 'multienum' && (node.op === 'in' || node.op === 'notIn') ? `dynamic.op.multienum.${node.op}` : `dynamic.op.${node.op}`);
    return [fieldLabel(node.field), op, node.value === undefined ? '' : valueText(f, node.value)].filter(Boolean).join(' ');
  };
  return summary(n);
}
