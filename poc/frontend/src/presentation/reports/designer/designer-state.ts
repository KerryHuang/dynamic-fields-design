import sharedOperators from '../../../../../shared/filter-operators.json';
import { operatorNeedsValue } from 'src/domain/dynamic/condition';
import type {
  CatalogField, ModuleCatalog, ReportChild, ReportDateRange, ReportDefinition, ReportFieldRef, ReportFilter, ReportMeasure, ReportSort, ReportViz,
} from 'src/domain/dynamic/report-types';
import type { FilterOperator } from 'src/domain/dynamic/types';
import { findChild, listFields, refKey } from '../report-catalog';

export type DesignerStep = 0 | 1 | 2 | 3 | 4;

export interface DesignerState {
  name: string;
  mainModuleKey: string | null;
  child: ReportChild | null;
  columns: ReportFieldRef[];
  groups: { field: ReportFieldRef; bucket?: string | null }[];
  measures: ReportMeasure[];
  filters: ReportFilter[];
  primaryDateField: ReportFieldRef | null;
  dateRange: ReportDateRange | null;
  sort: ReportSort[];
  viz: ReportViz;
  /** 載入的定義含被遮蔽的條件值（後端依觀看者遮蔽）；存回去會得到 400，所以永遠不能存。 */
  hasHidden: boolean;
}

const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;

export function emptyState(): DesignerState {
  return {
    name: '', mainModuleKey: null, child: null, columns: [], groups: [], measures: [], filters: [],
    primaryDateField: null, dateRange: null, sort: [], viz: 'table', hasHidden: false,
  };
}

export function fromDefinition(def: ReportDefinition, name = ''): DesignerState {
  const d = clone(def);
  return {
    name, mainModuleKey: d.mainModuleKey, child: d.child ?? null, columns: d.columns, groups: d.groups, measures: d.measures,
    filters: d.filters, primaryDateField: d.primaryDateField ?? null, dateRange: d.dateRange ?? null, sort: d.sort ?? [], viz: d.viz,
    hasHidden: d.filters.some((f) => f.hidden) || d.measures.some((m) => m.condition?.hidden),
  };
}

export function toDefinition(s: DesignerState): ReportDefinition {
  const s2 = clone(s);
  const plain = (f: ReportFilter): ReportFilter => ({ field: f.field, op: f.op, ...(f.value === undefined ? {} : { value: f.value }) });
  return {
    version: 1, mainModuleKey: s2.mainModuleKey ?? '', child: s2.child, columns: s2.columns, groups: s2.groups,
    measures: s2.measures.map((m) => (m.condition ? { ...m, condition: plain(m.condition) } : m)),
    filters: s2.filters.map(plain), primaryDateField: s2.primaryDateField, dateRange: s2.dateRange, sort: s2.sort, viz: s2.viz,
  };
}

/** 運算子取自 poc/shared/filter-operators.json；日期結果的公式與彙總用 `:date` 版本。 */
export function operatorsOf(f: Pick<CatalogField, 'type' | 'isDate'>): FilterOperator[] {
  const table = sharedOperators as Record<string, FilterOperator[]>;
  return table[(f.type === 'formula' || f.type === 'rollup') && f.isDate ? `${f.type}:date` : f.type] ?? [];
}

const isChild = (r: ReportFieldRef | null | undefined) => r?.source === 'child';
const measureUsesChild = (m: ReportMeasure) => m.kind === 'countChild' || isChild(m.field) || isChild(m.condition?.field);

export function setMainModule(s: DesignerState, key: string | null) {
  if (s.mainModuleKey === key) return;
  Object.assign(s, { ...emptyState(), name: s.name, viz: s.viz, mainModuleKey: key });
}

/** 有設定內容（改主模組會清掉）。 */
export function hasContent(s: DesignerState): boolean {
  return !!s.child || s.columns.length + s.groups.length + s.measures.length + s.filters.length > 0 || !!s.primaryDateField || !!s.dateRange;
}

export function setChild(s: DesignerState, child: ReportChild | null) {
  if (s.child?.moduleKey === child?.moduleKey && s.child?.linkFieldId === child?.linkFieldId) return;
  s.child = child;
  for (let i = s.columns.length - 1; i >= 0; i--) if (isChild(s.columns[i])) removeColumn(s, i);
  for (let i = s.groups.length - 1; i >= 0; i--) if (isChild(s.groups[i]!.field)) removeGroup(s, i);
  for (let i = s.measures.length - 1; i >= 0; i--) if (measureUsesChild(s.measures[i]!)) removeMeasure(s, i);
  s.filters = s.filters.filter((f) => !isChild(f.field));
  if (isChild(s.primaryDateField)) setPrimaryDate(s, null);
}

/** 日期區間必須有主要日期欄位，所以清掉欄位時一併清掉區間。 */
export function setPrimaryDate(s: DesignerState, ref: ReportFieldRef | null) {
  s.primaryDateField = ref;
  if (!ref) s.dateRange = null;
}

/** 量值 Id 取比現有量值與 sort 提到的都大的號碼，避免新量值繼承舊量值留下的排序。 */
export function addMeasure(s: DesignerState, kind: string, field: ReportFieldRef | null, condition: ReportFilter | null = null) {
  const ids = [...s.measures.map((m) => m.id), ...s.sort.map((x) => x.key)];
  const next = Math.max(0, ...ids.map((id) => Number(/^m(\d+)$/.exec(id)?.[1] ?? 0))) + 1;
  s.measures.push({ id: `m${next}`, kind, field, condition });
}

const dropSort = (s: DesignerState, key: string) => { s.sort = s.sort.filter((x) => x.key !== key); };
const stillUsed = (s: DesignerState, key: string) => [...s.columns, ...s.groups.map((g) => g.field)].some((r) => refKey(r) === key);
export function removeColumn(s: DesignerState, index: number) {
  const [r] = s.columns.splice(index, 1);
  if (r && !stillUsed(s, refKey(r))) dropSort(s, refKey(r));
}
export function removeGroup(s: DesignerState, index: number) {
  const [g] = s.groups.splice(index, 1);
  if (g && !stillUsed(s, refKey(g.field))) dropSort(s, refKey(g.field));
}
export function removeMeasure(s: DesignerState, index: number) {
  const [m] = s.measures.splice(index, 1);
  if (m) dropSort(s, m.id);
}

const filled = (v: unknown) => v !== undefined && v !== null && v !== '';
function filterComplete(f: ReportFilter): boolean {
  const v = f.value;
  switch (operatorNeedsValue(f.op as FilterOperator)) {
    case 'none': return true;
    case 'pair': return Array.isArray(v) && v.length === 2 && v.every(filled);
    case 'list': return Array.isArray(v) && v.length > 0;
    case 'days': return typeof v === 'number' && Number.isInteger(v) && v >= 0;
    default: return filled(v);
  }
}
/** 需要 N 的區間種類，與後端 ReportRules.LastKinds 相同；其他 last* 開頭的（lastWeek 等）不需要。 */
export const LAST_RANGE_KINDS = ['lastDays', 'lastWeeks', 'lastMonths'];
export const needsN = (r: Pick<ReportDateRange, 'kind'>) => LAST_RANGE_KINDS.includes(r.kind);

export function canAdvance(s: DesignerState, step: DesignerStep): boolean {
  switch (step) {
    case 0: return !!s.mainModuleKey;
    case 1: return true;
    case 2:
      return s.filters.every(filterComplete)
        && (!s.dateRange || (!!s.primaryDateField && (!needsN(s.dateRange) || (s.dateRange.n ?? 0) >= 1)));
    case 3:
      return s.columns.length + s.measures.length > 0 && (s.groups.length === 0 || s.measures.length > 0)
        && s.measures.every((m) => (['sum', 'avg', 'min', 'max'].includes(m.kind) ? !!m.field : m.kind === 'ratio' ? !!m.condition && filterComplete(m.condition) : true));
    default: return false;
  }
}

export interface Unavailable {
  columns: number[]; groups: number[]; measures: number[]; filters: number[]; sort: number[]; primaryDate: boolean; child: boolean; any: boolean;
}

/** 已存的設定用到目錄沒有的欄位（被刪、停用，或目標模組不可讀）：標為不可用，使用者移除前不能存。 */
export function unavailableRefs(s: DesignerState, catalog: ModuleCatalog): Unavailable {
  const known = new Set(listFields(catalog, s.child, '').map((e) => refKey(e.ref)));
  const missing = (r: ReportFieldRef | null | undefined) => !!r && !known.has(refKey(r));
  const idx = <T>(items: T[], bad: (x: T) => boolean) => items.flatMap((x, i) => (bad(x) ? [i] : []));
  const u: Unavailable = {
    child: !!s.child && !findChild(catalog, s.child),
    columns: idx(s.columns, missing),
    groups: idx(s.groups, (g) => missing(g.field)),
    measures: idx(s.measures, (m) => missing(m.field) || missing(m.condition?.field) || (m.kind === 'countChild' && !findChild(catalog, s.child))),
    filters: idx(s.filters, (f) => missing(f.field)),
    // sort 的鍵不是量值 Id 時是欄位鍵（source:fieldId:via）；其他字串後端忽略
    sort: idx(s.sort, (x) => !s.measures.some((m) => m.id === x.key) && /^\w+:\d+:\d*$/.test(x.key) && !known.has(x.key)),
    primaryDate: missing(s.primaryDateField),
    any: false,
  };
  u.any = u.child || u.primaryDate || [u.columns, u.groups, u.measures, u.filters, u.sort].some((l) => l.length > 0);
  return u;
}

export function canSave(s: DesignerState, catalog: ModuleCatalog): boolean {
  return s.name.trim().length > 0 && !s.hasHidden && !unavailableRefs(s, catalog).any && ([0, 1, 2, 3] as const).every((i) => canAdvance(s, i));
}
