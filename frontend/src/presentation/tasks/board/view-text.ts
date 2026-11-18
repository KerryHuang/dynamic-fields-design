import { toValue, type MaybeRefOrGetter } from 'vue';
import { useI18n } from 'vue-i18n';
import type { TaskViewKind } from 'src/domain/dynamic/task-board-types';

/**
 * 存檔檢視元件（選擇器、設定、分享）看板與甘特圖共用：字一律用 `taskBoard.<鍵>`，甘特圖有自己寫法的（含「看板」二字的、
 * 甘特圖專有的欄位）放在 `taskGantt.views.<鍵>`，同一個鍵優先用它。
 */
export function useViewText(kind: MaybeRefOrGetter<TaskViewKind>) {
  const { t, te } = useI18n();
  return (key: string, named?: Record<string, unknown>): string => {
    const gantt = `taskGantt.views.${key}`;
    const k = toValue(kind) === 'gantt' && te(gantt) ? gantt : `taskBoard.${key}`;
    return named ? t(k, named) : t(k);
  };
}
