import type { DynamicRecord, SchemaField } from 'src/domain/dynamic/types';

/** Text (and link, for module targets) of a reference value; null when the record has no value for the field. */
export function referenceView(
  record: DynamicRecord, field: SchemaField, t: (key: string) => string,
): { text: string; to?: string } | null {
  const info = record.references?.[field.key];
  if (!info) return null;
  if (info.state === 'missing') return { text: t('dynamic.reference.missing') };
  if (info.state === 'restricted') return { text: t('dynamic.reference.restricted') };
  const text = info.title ?? info.key;
  return field.reference?.kind === 'module' ? { text, to: `/dynamic/${field.reference.key}/${info.key}` } : { text };
}
