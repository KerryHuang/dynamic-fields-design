import { useQueryClient } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalActionResult } from 'src/domain/dynamic/approval-types';
import { joinList } from 'src/i18n/translate';
import { isStale } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import { resultNotice } from './approval-actions';

interface Options {
  recordId: () => number;
  open: () => boolean;
  close: () => void;
  /** Sends the request with the key of this opening. */
  send: (idempotencyKey: string) => Promise<ApprovalActionResult>;
}

/**
 * Shared behaviour of the approve, reject and withdraw dialogs: one Idempotency-Key per opening (kept on a retry after a
 * network error or 5xx, replaced after a success or a 422), the server's field errors, and the 409 cases.
 */
export function useApprovalAction(o: Options) {
  const { t, locale } = useI18n();
  const $q = useQuasar();
  const qc = useQueryClient();
  const pending = ref(false);
  const topError = ref('');
  const stale = ref(false);
  const fieldErrors = ref<Record<string, string>>({});
  let key = newIdempotencyKey();

  watch(o.open, (open) => {
    if (!open) return;
    key = newIdempotencyKey();
    topError.value = '';
    stale.value = false;
    fieldErrors.value = {};
  }, { immediate: true });

  const reload = () => Promise.all([
    qc.invalidateQueries({ queryKey: ['record', o.recordId()] }),
    qc.invalidateQueries({ queryKey: ['record-approval', o.recordId()] }),
  ]);

  async function submit() {
    topError.value = '';
    stale.value = false;
    fieldErrors.value = {};
    pending.value = true;
    try {
      const result = await o.send(key);
      key = newIdempotencyKey();
      const n = resultNotice(result, locale.value, t);
      $q.notify({ type: n.type, message: n.message, timeout: n.type === 'warning' ? 8000 : undefined });
      o.close();
    } catch (e) {
      const err = e as ApiError;
      if (err.status === 422) key = newIdempotencyKey();
      if (err.status === 400 && err.errors) {
        const rest: string[] = [];
        for (const [k, v] of Object.entries(err.errors)) {
          if (k === 'comment' || k === 'to') fieldErrors.value[k] = joinList(v);
          else rest.push(...v);
        }
        topError.value = rest.join('；');
      } else if (err.status === 409 && isStale(err)) {
        stale.value = true;
        topError.value = t('dynamic.stale');
        void reload();
      } else if (err.status === 409) {
        // Already handled by someone else, already pending, no approver: the server says what; the page shows the new state.
        $q.notify({ type: 'warning', message: err.message ?? t('dynamic.loadFailed') });
        void reload();
        o.close();
      } else topError.value = err.message ?? t('dynamic.loadFailed');
    } finally {
      pending.value = false;
    }
  }

  return { pending, topError, stale, fieldErrors, submit, reload };
}
