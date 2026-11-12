import { useQueryClient } from '@tanstack/vue-query';
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalActionResult } from 'src/domain/dynamic/approval-types';
import { joinList } from 'src/i18n/translate';
import { isStale } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import { useEffectivePolicies } from 'src/application/dynamic/queries/policies-queries';
import { resultNotice } from './approval-actions';

interface Options {
  recordId: () => number;
  moduleKey: () => string;
  close: () => void;
  /** Request keys that belong to a field on the dialog (besides the comment): their 400 shows under that field. */
  fieldKeys?: () => string[];
  /** Sends the request with the key of this opening. */
  send: (idempotencyKey: string) => Promise<ApprovalActionResult>;
}

/**
 * Shared behaviour of the approve, reject and withdraw dialogs: one Idempotency-Key per opening (kept on a retry after a
 * network error or 5xx, replaced after a 422; every opening mounts the dialog again, so a success needs no new one),
 * the comment limit from the policy, the server's field errors, and the 409 cases.
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

  // The limit comes from the policy; without a value no maxlength is set and the server's 400 is the only guard.
  const policies = useEffectivePolicies(o.moduleKey);
  const maxComment = computed(() => {
    const v = policies.data.value?.values['approval.maxCommentLength'];
    return typeof v === 'number' ? v : undefined;
  });

  const reload = () => Promise.all([
    qc.invalidateQueries({ queryKey: ['record', o.recordId()] }),
    qc.invalidateQueries({ queryKey: ['record-approval', o.recordId()] }),
    qc.invalidateQueries({ queryKey: ['approval-inbox'] }),
  ]);

  async function submit() {
    topError.value = '';
    stale.value = false;
    fieldErrors.value = {};
    pending.value = true;
    try {
      const result = await o.send(key);
      const n = resultNotice(result, locale.value, t);
      $q.notify({ type: n.type, message: n.message, timeout: n.type === 'warning' ? 8000 : undefined });
      o.close();
    } catch (e) {
      const err = e as ApiError;
      if (err.status === 422) key = newIdempotencyKey();
      if (err.status === 400 && err.errors) {
        const rest: string[] = [];
        const own = o.fieldKeys?.() ?? [];
        for (const [k, v] of Object.entries(err.errors)) {
          if (k === 'comment' || own.includes(k)) fieldErrors.value[k] = joinList(v);
          else rest.push(...v);
        }
        topError.value = joinList(rest);
      } else if (err.status === 409 && isStale(err)) {
        stale.value = true;
        topError.value = t('dynamic.stale');
        void reload();
      } else if (err.status === 409) {
        // Already handled by someone else, already pending, no approver: the server says what; the page shows the new state.
        $q.notify({ type: 'warning', message: err.message ?? t('designer.saveFailed') });
        void reload();
        o.close();
      } else topError.value = err.message ?? t('designer.saveFailed');
    } finally {
      pending.value = false;
    }
  }

  return { pending, topError, stale, fieldErrors, maxComment, submit, reload };
}
