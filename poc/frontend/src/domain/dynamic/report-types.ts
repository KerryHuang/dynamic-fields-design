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
export interface ReportDefinition {
  version: number;
  mainModuleKey: string;
  columns: ReportFieldRef[];
  groups: { field: ReportFieldRef; bucket?: string | null }[];
  measures: ReportMeasure[];
  filters: ReportFilter[];
  primaryDateField?: ReportFieldRef | null;
  dateRange?: ReportDateRange | null;
  viz: ReportViz;
}
export interface ReportDetail extends ReportItem { definition: ReportDefinition }
