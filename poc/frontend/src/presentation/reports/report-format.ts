import type { ReportColumn, ReportGroupResult } from 'src/domain/dynamic/report-types';

const EPOCH_DAY_NUMBER = 719162; // DateOnly(1970-01-01).DayNumber

/** DateOnly.DayNumber（0001-01-01 為 0）換成 yyyy-MM-dd。 */
export function dayNumberToDate(n: number): string {
  return new Date((n - EPOCH_DAY_NUMBER) * 86_400_000).toISOString().slice(0, 10);
}

/** 量值與明細格的顯示：ratio 是 0 到 1 的比例；dayNumber 換成日期；沒有值顯示破折號。 */
export function formatValue(v: string | number | null | undefined, valueType: string, locale: string): string {
  if (v === null || v === undefined || v === '') return '—';
  if (typeof v === 'string') return v;
  switch (valueType) {
    case 'dayNumber': return dayNumberToDate(v);
    case 'ratio': return `${(v * 100).toLocaleString(locale, { maximumFractionDigits: 1 })}%`;
    case 'percent': return `${v.toLocaleString(locale, { maximumFractionDigits: 4 })}%`;
    default: return v.toLocaleString(locale, { maximumFractionDigits: 4 });
  }
}

/** 實體標題欄位的 id 固定為 0（鍵為 source:fieldId:via），後端 label 寫死「標題」，依語系翻譯。 */
export function columnLabel(c: ReportColumn, titleLabel: string): string {
  return c.key.split(':')[1] === '0' && c.label.endsWith('標題') ? c.label.slice(0, -2) + titleLabel : c.label;
}

/** 多個分組欄位的標籤合成一個類別名稱。 */
export const groupName = (g: ReportGroupResult) => g.groupLabels.join(' / ');
