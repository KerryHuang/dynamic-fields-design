<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAuditEntry } from 'src/application/dynamic/queries/ops-queries';
import { auditDiff, formatTime, labelOr } from 'src/domain/ops/ops-format';

// 稽核詳細（維運設計 §17.3 稽核頁）：全部欄位＋摘要；差異以「欄位：舊值 → 新值」列出，其餘結構化摘要縮排顯示。
// 沒有 clientIp（Demo 站）時不顯示該欄。
const props = defineProps<{ auditId: number }>();
const emit = defineEmits<{ close: [] }>();
const { t, te } = useI18n();
const q = useAuditEntry(() => props.auditId);
const a = computed(() => q.data.value);
const parts = computed(() => auditDiff(a.value?.summary));
const rest = computed(() => (Object.keys(parts.value.rest).length ? JSON.stringify(parts.value.rest, null, 2) : ''));
const fields = computed(() => {
  const x = a.value;
  if (!x) return [];
  const rows: [string, string, string][] = [
    ['time', t('audit.cols.time'), formatTime(x.occurredUtc)],
    ['actor', t('audit.cols.actor'), x.actorName ? `${x.actorName}（${x.actorUserId}）` : x.actorUserId ?? t(`audit.actorKind.${x.actorKind}`)],
    ['action', t('audit.cols.action'), `${labelOr(t, te, `audit.actions.${x.action}`, x.action)}（${x.action}）`],
    ['risk', t('audit.cols.risk'), labelOr(t, te, `audit.risk.${x.riskLevel}`, x.riskLevel)],
    ['target', t('audit.cols.target'), [x.targetKind && labelOr(t, te, `audit.targetKind.${x.targetKind}`, x.targetKind), x.targetLabel ?? x.targetId]
      .filter(Boolean).join('：') || '—'],
    ['result', t('audit.cols.result'), labelOr(t, te, `audit.result.${x.result}`, x.result) + (x.reasonCode ? `（${labelOr(t, te, `audit.reason.${x.reasonCode}`, x.reasonCode)}）` : '')],
    ['channel', t('audit.cols.channel'), labelOr(t, te, `audit.channel.${x.channel}`, x.channel)],
  ];
  if (x.requestId) rows.push(['requestId', t('audit.cols.requestId'), x.requestId]);
  if (x.clientIp) rows.push(['clientIp', t('audit.cols.clientIp'), x.clientIp]);
  if (x.resultCount !== undefined) rows.push(['resultCount', t('audit.cols.resultCount'), String(x.resultCount)]);
  return rows;
});
</script>

<template>
  <q-card flat bordered class="column no-wrap full-height" data-testid="audit-detail">
    <q-card-section class="row no-wrap items-center">
      <div class="col text-subtitle1">{{ t('audit.detail') }}</div>
      <q-btn flat round dense icon="close" :aria-label="t('designer.close')" data-testid="audit-detail-close" @click="emit('close')" />
    </q-card-section>
    <q-separator />
    <q-card-section v-if="q.isError.value" class="text-negative">{{ t('ops.loadFailed') }}</q-card-section>
    <q-card-section v-else-if="!a" class="text-muted">{{ t('designer.loading') }}</q-card-section>
    <q-card-section v-else class="col scroll column no-wrap q-gutter-y-md">
      <q-list dense bordered separator>
        <q-item v-for="[k, label, v] in fields" :key="k" :data-testid="`audit-field-${k}`">
          <q-item-section>
            <q-item-label caption>{{ label }}</q-item-label>
            <q-item-label style="overflow-wrap: anywhere">{{ v }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
      <div v-if="parts.diff.length" data-testid="audit-diff">
        <div class="text-subtitle2">{{ t('audit.changes') }}</div>
        <div v-for="d in parts.diff" :key="d.field" class="audit-diff-line">
          <span v-if="d.field" class="text-weight-medium">{{ d.field }}：</span>{{ d.from || '∅' }} → {{ d.to || '∅' }}
        </div>
      </div>
      <div v-if="rest">
        <div class="text-subtitle2">{{ t('audit.summary') }}</div>
        <pre class="audit-pre" data-testid="audit-summary">{{ rest }}</pre>
      </div>
    </q-card-section>
  </q-card>
</template>

<style scoped>
.audit-diff-line { overflow-wrap: anywhere; }
.audit-pre {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  margin: 0;
}
</style>
