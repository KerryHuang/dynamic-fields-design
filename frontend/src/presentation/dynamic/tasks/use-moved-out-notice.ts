import { useQuasar } from 'quasar';
import { useI18n } from 'vue-i18n';
import { useUndoReassign } from 'src/application/dynamic/queries/tasks-queries';
import type { ApiError } from 'src/services/api/http';

/** How long the "moved out of your scope" toast stays (a presentation choice; the server token lives 2 minutes). */
export const MOVED_OUT_NOTICE_MS = 10_000;

/**
 * The toast shown after a manager hands a task to someone outside their scope: "moved out of your scope" with an Undo
 * action. Shared by the list, the task dialog and the board. Undo puts the assignee back like any other reassignment
 * (notifications already sent stay); a rejected token shows the failure message.
 */
export function useMovedOutNotice() {
  const $q = useQuasar();
  const { t } = useI18n();
  const undo = useUndoReassign();

  function show(taskId: number, undoToken: string | null) {
    if (!undoToken) {
      $q.notify({ type: 'info', message: t('tasks.movedOut'), position: 'bottom', timeout: MOVED_OUT_NOTICE_MS });
      return;
    }
    $q.notify({
      type: 'info',
      message: t('tasks.movedOut'),
      position: 'bottom',
      timeout: MOVED_OUT_NOTICE_MS,
      actions: [{
        label: t('tasks.undo'),
        color: 'white',
        noCaps: true,
        handler: async () => {
          try {
            await undo.mutateAsync({ id: taskId, undoToken });
            $q.notify({ type: 'positive', message: t('tasks.undone'), position: 'bottom' });
          } catch (e) {
            $q.notify({ type: 'negative', message: (e as ApiError).status === 400 ? t('tasks.undoFailed') : (e as ApiError).message || t('tasks.undoFailed'), position: 'bottom' });
          }
        },
      }],
    });
  }

  return { show };
}
