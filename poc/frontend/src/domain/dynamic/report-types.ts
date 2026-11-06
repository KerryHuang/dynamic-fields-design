/** 對應後端 ReportService/Data/ReportResponses.cs 與 Domain/Reports/ReportDefinition.cs。 */

export type ReportState = 'ok' | 'empty' | 'restricted' | 'fieldMissing' | 'moduleDisabled' | 'tooMany';
export type ReportViz = 'table' | 'indicator' | 'line' | 'bar' | 'donut';

/** Role: group|measure|field；ValueType: text|number|percent|date|dateTime|ratio|dayNumber（DayNumber 為 DateOnly.DayNumber）。 */
export interface ReportColumn { key: string; label: string; role: 'group' | 'measure' | 'field'; valueType: string }
export interface ReportGroupResult { groupKeys: string[]; groupLabels: string[]; values: Record<string, number | null> }
export interface ReportAppliedRange { from: string; to: string; kind: string; n?: number | null }
export interface ReportDateRange { kind: string; n?: number | null }

export interface ReportResult {
  state: ReportState;
  message?: string | null;
  rowMeaning: string;
  columns: ReportColumn[];
  groups?: ReportGroupResult[] | null;
  total?: Record<string, number | null> | null;
  /** 每列順序對齊 columns 中 role 為 field 的欄。 */
  detailRows?: (string | number | null)[][] | null;
  dedupedNote: boolean;
  basisDate: string;
  appliedRange?: ReportAppliedRange | null;
}
export interface DrillResult extends ReportResult { truncated: boolean }

export interface ReportItem {
  id: number;
  name: string;
  ownerName: string;
  isOwner: boolean;
  sharedGroupIds: number[];
  mainModuleKey: string;
}

export interface ReportFieldRef { source: string; fieldId: number; viaFieldId?: number | null }
/** hidden 為 true 時觀看者看不到條件值（後端遮蔽），沒有 value。 */
export interface ReportFilter { field: ReportFieldRef; op: string; value?: unknown; hidden?: boolean }
export interface ReportMeasure { id: string; kind: string; field?: ReportFieldRef | null; condition?: ReportFilter | null }
export interface ReportChild { moduleKey: string; linkFieldId: number }
export interface ReportSort { key: string; desc: boolean }
export interface ReportDefinition {
  version: number;
  mainModuleKey: string;
  child?: ReportChild | null;
  columns: ReportFieldRef[];
  groups: { field: ReportFieldRef; bucket?: string | null }[];
  measures: ReportMeasure[];
  filters: ReportFilter[];
  primaryDateField?: ReportFieldRef | null;
  dateRange?: ReportDateRange | null;
  sort?: ReportSort[];
  viz: ReportViz;
}
export interface ReportDetail extends ReportItem { definition: ReportDefinition }

/** 對應後端 GetReportCatalogHandler 的回應；旗標由後端決定，前端不重算。 */
export interface CatalogOption { id: string; label: string; active: boolean }
export interface CatalogField {
  id: number; label: string; type: string; canGroup: boolean; canSum: boolean; canMinMax: boolean; isDate: boolean;
  options?: CatalogOption[] | null;
  /** 參照欄位的目標（module 或 entity）與鍵，挑選器據此決定 light 網址；非參照欄位沒有。 */
  refKind?: string | null; refKey?: string | null;
}
export interface CatalogVia { fieldId: number; label: string; targetKey: string; fields: CatalogField[] }
export interface CatalogChild {
  moduleKey: string; name: string; linkFieldId: number; linkLabel: string; isDetail: boolean; fields: CatalogField[]; via: CatalogVia[];
}
export interface ModuleCatalog { fields: CatalogField[]; via: CatalogVia[]; children: CatalogChild[] }
export interface CatalogModule { key: string; name: string }
