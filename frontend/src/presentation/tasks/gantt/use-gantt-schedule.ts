import { useQuasar } from 'quasar';
import { ref, type MaybeRefOrGetter } from 'vue';
import { useI18n } from 'vue-i18n';
import { useScheduleTask } from 'src/application/dynamic/queries/task-board-queries';
import type { BoardCard, GanttSource } from 'src/domain/dynamic/task-board-types';
import type { ApiError } from 'src/services/api/http';

/** 欄位下顯示的錯誤（伺服器 400 掛在 startDate／dueDate 時）。 */
export interface ScheduleFieldErrors { startDate?: string; dueDate?: string }
export type ScheduleOutcome = { ok: true; card: BoardCard } | { ok: false; fields: ScheduleFieldErrors | null };

/**
 * 甘特圖改日期的流程（日期對話框用，gt2b2 拖拉放開時也用同一個）：
 * - 同一個任務的請求沒回來前不再送（版本已經換了，再送一定 409），回 null。
 * - 409 stale 提示「任務已被別人修改，已重新整理」（查詢由 useScheduleTask 重抓）；凍結、403、404 與其他失敗也用通知，文字一律由介面語言決定。
 * - 400 掛在 startDate／dueDate 的錯誤不跳通知，回給呼叫端掛在欄位下（拖拉沒有欄位，自己決定怎麼顯示）。
 */
export function useGanttSchedule(source: MaybeRefOrGetter<GanttSource>) {
  const { t } = useI18n();
  const $q = useQuasar();
  const mutation = useScheduleTask(source);
  const pending = ref(new Set<number>());

  function fieldErrors(err: ApiError): ScheduleFieldErrors | null {
    if (err.status !== 400) return null;
    const fields: ScheduleFieldErrors = {};
    // 開始日的 400 只有「開始日晚於到期日」一種（ScheduleTaskHandler），用介面語言的同一句
    if (err.errors?.startDate) fields.startDate = t('tasks.startAfterDue');
    if (err.errors?.dueDate) fields.dueDate = err.errors.dueDate[0] || t('taskGantt.schedule.failed');
    return fields.startDate || fields.dueDate ? fields : null;
  }
  function failureMessage(err: ApiError): string {
    if (err.status === 409) return err.code === 'stale' ? t('taskBoard.stale') : t('taskGantt.schedule.frozen');
    if (err.status === 404) return t('tasks.notFound');
    if (err.status === 403) return t('taskGantt.schedule.noPermission');
    return t('taskGantt.schedule.failed');
  }

  async function schedule(card: BoardCard, startDate: string | null, dueDate: string | null): Promise<ScheduleOutcome | null> {
    if (pending.value.has(card.id)) return null;
    pending.value = new Set(pending.value).add(card.id);
    try {
      const result = await mutation.mutateAsync({ card, startDate, dueDate });
      return { ok: true, card: result.card };
    } catch (e) {
      const fields = fieldErrors(e as ApiError);
      if (!fields) $q.notify({ type: 'negative', message: failureMessage(e as ApiError), position: 'bottom' });
      return { ok: false, fields };
    } finally {
      const next = new Set(pending.value);
      next.delete(card.id);
      pending.value = next;
    }
  }

  return { schedule, isPending: (id: number) => pending.value.has(id) };
}
