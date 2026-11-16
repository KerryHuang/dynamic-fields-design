import type { SchemaField } from 'src/domain/dynamic/types';

/** 欄位是否佔滿整列；沒設定時依型別預設：多行文字佔滿，其他不佔滿。 */
export const isFullWidth = (f: Pick<SchemaField, 'type' | 'config'>): boolean => f.config.fullWidth ?? f.type === 'textarea';

/**
 * 區塊內一個欄位格的 Quasar grid class。欄數依寬度遞減：xs（< 600px）1 欄；sm 最多 2 欄；md 最多 3 欄；
 * lg（>= 1440px）最多 4 欄；實際欄數是區塊設定與該寬度上限的較小值。
 * 佔滿整列的欄位用 col-12，自然換到新的一列。
 */
export function fieldColClass(columns: number, fullWidth: boolean): string {
  if (fullWidth || columns <= 1) return 'col-12';
  if (columns >= 4) return 'col-12 col-sm-6 col-md-4 col-lg-3';
  return columns >= 3 ? 'col-12 col-sm-6 col-md-4' : 'col-12 col-sm-6';
}
