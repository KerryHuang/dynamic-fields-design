import type { ReportDateRange, ReportResult, ReportViz } from './report-types';
/** 對應後端 DashboardService/Data/DashboardResponses.cs 與 Domain/Reports/DashboardLayout.cs。 */

export interface CardOptions {
  range?: ReportDateRange | null;
  sortKey?: string | null;
  sortDesc: boolean;
  topN?: number | null;
  cumulative: boolean;
  comparePeriods?: number | null;
  linkDashboardId?: number | null;
}

/** 版面是 12 欄的格子；x、y、w、h 的單位是欄與列。 */
export interface DashboardCard {
  id: string;
  reportId: number;
  title?: string | null;
  viz: ReportViz;
  x: number;
  y: number;
  w: number;
  h: number;
  options: CardOptions;
}
export interface DashboardLayout { cards: DashboardCard[] }

export interface DashboardListItem { id: number; name: string; version: number; adjusted: boolean; hasUpdate: boolean }
export interface MyDashboardItem { id: number; name: string }
export interface SharedReportItem { id: number; name: string; ownerName: string }
export interface DashboardList { assigned: DashboardListItem[]; mine: MyDashboardItem[]; sharedReports: SharedReportItem[] }

export interface DashboardDetail {
  id: number;
  name: string;
  kind: 'assigned' | 'mine';
  version: number;
  adjusted: boolean;
  hasUpdate: boolean;
  layout: DashboardLayout;
  /** 卡片引用的報表名稱（key 是報表 id）；看不到定義的報表沒有。 */
  reportNames: Record<string, string>;
}

export interface AdminDashboardItem { id: number; name: string; version: number; sequence: number; groupIds: number[]; cardCount: number }
export interface AdminDashboardDetail { id: number; name: string; version: number; sequence: number; groupIds: number[]; layout: DashboardLayout }

/** 卡片查詢結果：報表結果加上卡片選項算出的部分。 */
export interface CardResult extends ReportResult {
  /** 對齊 groups，第一個量值的累計占比（百分比）。 */
  cumulative?: (number | null)[] | null;
  compare?: { previousAverage?: number | null; deltaPercent?: number | null } | null;
  /** 畫面送了日期篩選但這張卡片沒有採用。 */
  ignoresDateFilter?: boolean;
}

export const DEFAULT_CARD_OPTIONS: CardOptions = { sortDesc: false, cumulative: false };
