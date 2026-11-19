<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useIncident } from 'src/application/dynamic/queries/ops-queries';
import { formatHealthValue, formatTime, incidentTitle, labelOr, SEVERITY_ICON } from 'src/domain/ops/ops-format';
import type { IncidentSummary } from 'src/domain/ops/ops-types';

// 事件詳細（維運設計 §17.3 第 2 項）：手機全螢幕、其他寬度右側抽屜；樣本的原文與 stack 等寬字型、預設摺疊、區塊自己橫捲
const props = defineProps<{ incidentId: number | null }>();
const emit = defineEmits<{ close: []; acknowledge: [i: IncidentSummary]; resolve: [i: IncidentSummary] }>();
const { t, te, locale } = useI18n();
const $q = useQuasar();
const query = useIncident(() => props.incidentId);
const d = computed(() => query.data.value);

const fields = computed(() => {
  const i = d.value;
  if (!i) return [];
  return [
    [t('ops.incidents.id'), `#${i.id}`],
    [t('ops.incidents.status'), t(`ops.status.${i.status}`)],
    [t('ops.incidents.severity'), t(`ops.severity.${i.severity}`)],
    [t('ops.incidents.category'), t(`ops.category.${i.category}`)],
    [t('ops.incidents.handler'), t(`ops.handler.${i.handler}`)],
    [t('ops.incidents.source'), labelOr(t, te, `ops.source.${i.source}`, i.source)],
    [t('ops.incidents.code'), i.code],
    [t('ops.incidents.count'), String(i.occurrenceCount)],
    [t('ops.incidents.firstSeen'), formatTime(i.firstSeenUtc)],
    [t('ops.incidents.lastSeen'), formatTime(i.lastSeenUtc)],
    [t('ops.incidents.versions'), i.firstVersion === i.lastVersion ? i.lastVersion : `${i.firstVersion} → ${i.lastVersion}`],
    ...(i.reopenCount ? [[t('ops.incidents.reopenCount'), String(i.reopenCount)]] : []),
    ...(i.moduleKey ? [[t('ops.incidents.module'), i.moduleKey]] : []),
    ...(i.route ? [[t('ops.incidents.route'), i.route]] : []),
    ...(i.acknowledgedUtc ? [[t('ops.incidents.acknowledgedAt'), `${formatTime(i.acknowledgedUtc)}（${i.acknowledgedBy ?? ''}）`]] : []),
    ...(i.resolvedUtc
      ? [[t('ops.incidents.resolvedAt'), `${formatTime(i.resolvedUtc)}（${i.resolvedBy ?? t('ops.incidents.system')}；${
        i.resolvedReason ? labelOr(t, te, `ops.resolvedReason.${i.resolvedReason}`, i.resolvedReason) : ''}）`]]
      : []),
  ] as [string, string][];
});
</script>

<template>
  <q-dialog
    :model-value="incidentId !== null" :maximized="$q.screen.lt.sm" :position="$q.screen.lt.sm ? 'standard' : 'right'" full-height
    @update:model-value="(v) => { if (!v) emit('close'); }"
  >
    <q-card class="incident-detail column no-wrap" data-testid="incident-detail">
      <q-card-section class="row no-wrap items-start q-gutter-sm">
        <q-icon v-if="d" :name="SEVERITY_ICON[d.severity].icon" :color="SEVERITY_ICON[d.severity].color" size="sm" />
        <div class="col text-subtitle1" style="overflow-wrap: anywhere; min-width: 0">
          {{ d ? incidentTitle(t, te, d.titleKey, d.titleParams) : t('designer.loading') }}
          <q-badge v-if="d?.regression" color="negative" class="q-ml-xs">{{ t('ops.incidents.regression') }}</q-badge>
        </div>
        <q-btn flat round dense icon="close" :aria-label="t('designer.close')" data-testid="incident-detail-close" @click="emit('close')" />
      </q-card-section>
      <q-separator />
      <q-card-section v-if="query.isError.value" class="text-negative">{{ t('ops.loadFailed') }}</q-card-section>
      <q-card-section v-else-if="d" class="col scroll column no-wrap q-gutter-y-md">
        <div class="row q-gutter-sm">
          <q-btn v-if="d.status === 'open'" color="primary" dense no-caps :label="t('ops.incidents.acknowledge')" data-testid="incident-detail-ack" @click="emit('acknowledge', d)" />
          <q-btn
            v-if="d.manualResolvable && d.status !== 'resolved'" outline color="primary" dense no-caps :label="t('ops.incidents.resolve')"
            data-testid="incident-detail-resolve" @click="emit('resolve', d)"
          />
        </div>
        <q-list dense bordered separator>
          <q-item v-for="[k, v] in fields" :key="k">
            <q-item-section>
              <q-item-label caption>{{ k }}</q-item-label>
              <q-item-label style="overflow-wrap: anywhere">{{ v }}</q-item-label>
            </q-item-section>
          </q-item>
        </q-list>
        <div v-if="d.normalizedMessage">
          <div class="text-caption text-muted">{{ t('ops.incidents.normalizedMessage') }}</div>
          <pre class="ops-pre" data-testid="incident-normalized">{{ d.normalizedMessage }}</pre>
        </div>
        <div v-if="d.instances?.length" data-testid="incident-instances">
          <div class="text-subtitle2">{{ t('ops.health.instances') }}</div>
          <q-list dense bordered separator>
            <q-item v-for="s in d.instances" :key="s.instanceId">
              <q-item-section>
                <q-item-label style="overflow-wrap: anywhere">{{ s.instanceId }}</q-item-label>
                <q-item-label caption>
                  {{ labelOr(t, te, `ops.healthStatus.${s.status}`, s.status) }} · {{ formatHealthValue(s.value, '', t, locale) }} · {{ formatTime(s.checkedUtc) }}
                </q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
        </div>
        <div>
          <div class="text-subtitle2">{{ t('ops.incidents.history') }}</div>
          <div v-if="!d.history.length" class="text-muted">{{ t('ops.incidents.noHistory') }}</div>
          <q-list v-else dense bordered separator data-testid="incident-history">
            <q-item v-for="h in d.history" :key="h.id">
              <q-item-section>
                <q-item-label>{{ labelOr(t, te, `audit.actions.${h.action}`, h.action) }}</q-item-label>
                <q-item-label caption>{{ formatTime(h.occurredUtc) }} · {{ h.actorName ?? h.actorUserId ?? t('ops.incidents.system') }}</q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
          <div v-if="d.historyTruncated" class="text-caption text-muted q-mt-xs" data-testid="incident-history-truncated">
            {{ t('ops.incidents.historyTruncated') }}
            <router-link :to="{ path: '/system/audit', query: { targetKind: 'incident', targetId: String(d.id) } }">{{ t('ops.incidents.toAudit') }}</router-link>
          </div>
        </div>
        <div v-if="d.samples.length">
          <div class="text-subtitle2">{{ t('ops.incidents.samples', { n: d.samples.length }) }}</div>
          <q-list bordered separator>
            <q-expansion-item
              v-for="(s, idx) in d.samples" :key="idx" dense :label="formatTime(s.occurredUtc)"
              :caption="[s.userId, s.version, s.requestId?.slice(0, 8)].filter(Boolean).join(' · ')" :data-testid="`incident-sample-${idx}`"
            >
              <div class="q-pa-sm column no-wrap q-gutter-y-xs">
                <div v-if="s.route" class="text-caption">{{ t('ops.incidents.route') }}：{{ s.route }}</div>
                <div v-if="s.browser || s.locale" class="text-caption">{{ [s.browser, s.locale].filter(Boolean).join(' · ') }}</div>
                <router-link v-if="s.auditLogId" :to="{ path: '/system/audit', query: { id: String(s.auditLogId) } }">{{ t('ops.incidents.toAuditEntry') }}</router-link>
                <pre v-if="s.rawMessage" class="ops-pre">{{ s.rawMessage }}</pre>
                <pre v-if="s.rawStack" class="ops-pre" data-testid="incident-stack">{{ s.rawStack }}</pre>
              </div>
            </q-expansion-item>
          </q-list>
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.incident-detail { width: 560px; max-width: 100vw; }
.ops-pre {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  white-space: pre;
  overflow-x: auto;
  max-width: 100%;
  margin: 0;
  padding: 8px;
  border: 1px solid var(--t-border);
  border-radius: 4px;
}
</style>
