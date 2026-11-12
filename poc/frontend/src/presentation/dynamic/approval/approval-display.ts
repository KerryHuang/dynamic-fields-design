import type { RecordApprovalSummary } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';

type Translate = (key: string, params?: Record<string, unknown>) => string;

/** Header badge text; null when the record has never been through an approval. */
export function badgeText(s: RecordApprovalSummary, locale: string, t: Translate): string | null {
  if (s.status === 'none') return null;
  if (s.status === 'pending' && s.stageSeq != null && s.stageName) {
    return t('approval.badge.pending', { seq: s.stageSeq, stage: resolveI18n(s.stageName, locale) });
  }
  return t(`approval.status.${s.status}`);
}

/** Quasar brand / semantic colour names only. */
export function badgeColor(status: RecordApprovalSummary['status']): 'warning' | 'positive' | 'negative' | 'muted' {
  if (status === 'pending') return 'warning';
  if (status === 'approved') return 'positive';
  if (status === 'rejected') return 'negative';
  return 'muted';
}
