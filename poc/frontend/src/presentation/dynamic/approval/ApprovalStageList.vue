<script setup lang="ts">
import { date } from 'quasar';
import { useI18n } from 'vue-i18n';
import type { ApprovalStageView } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { labelOf, stageColor, stageIcon } from './approval-display';

defineProps<{ stages: ApprovalStageView[] }>();
const { t, te, locale } = useI18n();
const timeText = (iso: string) => date.formatDate(new Date(iso), 'YYYY-MM-DD HH:mm');
const overdue = (s: ApprovalStageView) => s.status === 'active' && !!s.dueTime && new Date(s.dueTime).getTime() < Date.now();
</script>

<template>
  <q-timeline color="primary" class="q-pl-sm approval-stages">
    <q-timeline-entry
      v-for="s in [...stages].sort((a, b) => a.seq - b.seq)" :key="s.id" :icon="stageIcon(s.status)" :color="stageColor(s.status)"
      :data-testid="`stage-${s.id}`"
    >
      <template #title>
        <span class="approval-wrap">{{ resolveI18n(s.name, locale) }}</span>
        <span v-if="s.addedByName" class="approval-wrap text-body2 text-muted" data-testid="stage-added-by"> · {{ t('approval.panel.addedBy', { name: s.addedByName }) }}</span>
      </template>
      <template #subtitle>
        <span>{{ labelOf('approval.stageStatus', s.status, t, te) }}</span>
        <span v-if="s.tasks.length > 1 || s.mode === 'any'"> · {{ t(`approval.mode.${s.mode}`) }}</span>
        <span v-if="s.status === 'skipped' && s.skipReason"> · {{ labelOf('approval.skipReason', s.skipReason, t, te) }}</span>
      </template>
      <div v-if="s.status === 'queued' && !s.tasks.length && s.addSignNames?.length" class="text-body2 approval-wrap q-mb-xs" data-testid="stage-add-sign-to">
        {{ t('approval.panel.addSignTo', { names: s.addSignNames.join(t('common.listSeparator')) }) }}
      </div>
      <div v-for="k in s.tasks" :key="k.id" class="row items-center q-gutter-x-sm q-mb-xs" :data-testid="`task-${k.id}`">
        <span class="approval-wrap">{{ k.assigneeName }}</span>
        <q-badge v-if="k.reason === 'fallback'" outline color="warning" :label="t('approval.reason.fallback')" data-testid="task-fallback" />
        <q-badge v-else-if="k.reason !== 'resolved'" outline color="muted" :label="labelOf('approval.reason', k.reason, t, te)" />
        <span v-if="(k.reason === 'transferred' || k.reason === 'reassigned') && k.originalName" class="text-muted approval-wrap" data-testid="task-original">{{ t('approval.panel.originalUser', { name: k.originalName }) }}</span>
        <span class="text-muted">{{ labelOf('approval.taskStatus', k.status, t, te) }}</span>
        <span v-if="k.actedTime" class="text-muted">{{ timeText(k.actedTime) }}</span>
      </div>
      <div v-if="s.dueTime" class="row items-center q-gutter-x-sm text-caption text-muted">
        <span>{{ t('approval.panel.due', { time: timeText(s.dueTime) }) }}</span>
        <q-badge v-if="overdue(s)" color="negative" :label="t('approval.panel.overdue')" data-testid="stage-overdue" />
      </div>
    </q-timeline-entry>
  </q-timeline>
</template>

<style scoped>
.approval-wrap { overflow-wrap: anywhere; }
</style>
