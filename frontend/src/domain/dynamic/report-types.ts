import type { I18nText } from './types';
/** 對應後端 ReportService/Data/ReportResponses.cs 與 Domain/Reports/ReportDefinition.cs。 */

/** unshared（卡片的報表已不再分享給他）與 reportGone（報表已不存在）只會出現在儀表板卡片。 */
export type ReportState = 'ok' | 'empty' | 'restricted' | 'fieldMissing' | 'moduleDisabled' | 'tooMany' | 'unshared' | 'reportGone';
export type ReportViz = 'table' | 'indicator' | 'line' | 'bar' | 'donut';
/** 報表資料來源：module 是動態模組（缺省），tasks、taskEvents 是系統資料來源（看板設計 §7.1）。 */
export type ReportDataset = 'module' | 'tasks' | 'taskEvents';
/** GET /reports/datasets 的一筆。 */
export interface ReportDatasetItem { dataset: Exclude<ReportDataset, 'module'>; name: string }
/** 報表頁上方的暫時篩選（不存檔）：期間取代報表設定的區間，部門只有任務資料來源看（看板設計 §8.2）。 */
export interface ReportRunFilter { range: ReportDateRange | null; departmentIds: number[]; includeSubDepartments: boolean }
export const NO_RUN_FILTER: ReportRunFilter = { range: null, departmentIds: [], includeSubDepartments: true };

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
  /** 明細的總列數，含沒有回傳的列。 */
  detailTotal?: number | null;
  /** 明細超過畫面上限，detailRows 只有前面幾列；總計仍依全部列計算。下鑽另看 truncated。 */
  detailTruncated?: boolean;
  dedupedNote: boolean;
  basisDate: string;
  /** basisDate 與後端的今天不同（Demo 資料日期停在產生那天）。 */
  basisIsFrozen?: boolean;
  appliedRange?: ReportAppliedRange | null;
  /** 只有任務資料來源且有明細時有值，與 detailRows 逐列對齊（taskEvents 同一任務的兩列對回同一個 Id）。 */
  taskIds?: number[] | null;
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
  /** 省略等於 module；任務資料來源的 mainModuleKey 為空字串。 */
  dataset?: ReportDataset;
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
export interface ReportDetail extends ReportItem { definition: ReportDefinition; canExport: boolean }

/** 分享對話框的目標群組：只有 id 與名稱（對應後端 ReportShareTarget）。 */
export interface ReportShareTarget { id: number; name: I18nText }

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
