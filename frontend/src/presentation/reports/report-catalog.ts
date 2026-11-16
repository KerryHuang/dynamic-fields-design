import type { CatalogChild, CatalogField, ModuleCatalog, ReportChild, ReportFieldRef } from 'src/domain/dynamic/report-types';

/** 實體標題欄位（引用目標是既有實體時可帶出）的 id 固定為 0。 */
export const ENTITY_TITLE_FIELD_ID = 0;

/** 與後端 ReportCalculator.KeyOf 同一格式。 */
export const refKey = (r: ReportFieldRef) => `${r.source}:${r.fieldId}:${r.viaFieldId ?? ''}`;

export type FieldSection = 'main' | 'via' | 'child';
export interface FieldEntry { ref: ReportFieldRef; label: string; section: FieldSection; field: CatalogField }

export function findChild(catalog: ModuleCatalog, child: ReportChild | null | undefined): CatalogChild | undefined {
  return child ? catalog.children.find((c) => c.moduleKey === child.moduleKey && c.linkFieldId === child.linkFieldId) : undefined;
}

/** 目錄裡所有可選欄位，順序為主模組、帶出、子模組。 */
export function listFields(catalog: ModuleCatalog, child: ReportChild | null | undefined, titleLabel: string): FieldEntry[] {
  const name = (f: CatalogField) => (f.id === ENTITY_TITLE_FIELD_ID ? titleLabel : f.label);
  const entries: FieldEntry[] = catalog.fields.map((field) => ({ ref: { source: 'main', fieldId: field.id, viaFieldId: null }, label: name(field), section: 'main', field }));
  for (const v of catalog.via)
    for (const field of v.fields)
      entries.push({ ref: { source: 'main', fieldId: field.id, viaFieldId: v.fieldId }, label: `${v.label}．${name(field)}`, section: 'via', field });
  const c = findChild(catalog, child);
  if (c) {
    for (const field of c.fields)
      entries.push({ ref: { source: 'child', fieldId: field.id, viaFieldId: null }, label: `${c.name}．${name(field)}`, section: 'child', field });
    for (const v of c.via)
      for (const field of v.fields)
        entries.push({ ref: { source: 'child', fieldId: field.id, viaFieldId: v.fieldId }, label: `${c.name}．${v.label}．${name(field)}`, section: 'via', field });
  }
  return entries;
}

export function findEntry(entries: FieldEntry[], ref: ReportFieldRef | null | undefined): FieldEntry | undefined {
  if (!ref) return undefined;
  const k = refKey(ref);
  return entries.find((e) => refKey(e.ref) === k);
}

/** 選項 id 換成標籤；停用的選項加上標記。目錄裡找不到的值維持原樣。 */
export function valueText(value: unknown, field: CatalogField | undefined, inactiveMark: string): string {
  if (value === undefined || value === null) return '';
  const one = (v: unknown) => {
    const o = field?.options?.find((x) => x.id === v);
    return o ? (o.active ? o.label : `${o.label}${inactiveMark}`) : typeof v === 'string' ? v : JSON.stringify(v);
  };
  return Array.isArray(value) ? value.map(one).join(', ') : one(value);
}
