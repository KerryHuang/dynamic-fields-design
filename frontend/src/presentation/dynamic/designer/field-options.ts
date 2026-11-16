import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';

/** Select options (value = Field.Id) for the active fields of a designer module that pass `accept`. */
export function fieldOptions(
  module: ModuleDetail | undefined, locale: string, accept: (f: DesignerField) => boolean = () => true,
): { value: number; label: string }[] {
  return (module?.blocks ?? []).flatMap((b) => b.fields)
    .filter((f) => f.status === 'Active' && accept(f))
    .map((f) => ({ value: f.id, label: resolveI18n(f.label, locale) }));
}

/** Option ids differ per module, so enums can be neither copied nor filled. */
const NOT_COPYABLE: FieldType[] = ['enum', 'multienum'];
/** Computed by the system: never a write target. */
const COMPUTED: FieldType[] = ['formula', 'rollup', 'autonumber'];

export const copySource = (f: DesignerField) => !NOT_COPYABLE.includes(f.type);
export const copyTarget = (f: DesignerField) => !NOT_COPYABLE.includes(f.type) && !COMPUTED.includes(f.type);
/** Quantities are integer or decimal fields (the server rejects the rest). */
export const isQuantityField = (f: DesignerField) => f.type === 'integer' || f.type === 'decimal';
export const isIntegerField = (f: DesignerField) => f.type === 'integer';
