<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import type { PreviewStage } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { joinList } from 'src/i18n/translate';

/** `designer`: the designer's trial reads the pick stage as the initiator's later choice, not the viewer's. */
defineProps<{ stages: PreviewStage[]; designer?: boolean }>();
const { t, locale } = useI18n();
</script>

<template>
  <q-timeline color="primary" class="q-pl-sm" data-testid="preview-stages">
    <q-timeline-entry
      v-for="(s, i) in stages" :key="s.code ?? i" :title="resolveI18n(s.name, locale)" :data-testid="`preview-stage-${s.code ?? i}`"
      :icon="s.status === 'pick' ? 'how_to_reg' : s.status === 'conditional' ? 'rule' : 'person'"
    >
      <div v-if="s.status === 'conditional'" class="text-muted">{{ t('approval.submit.stageConditional') }}</div>
      <div v-else-if="s.status === 'pick'" class="text-muted">{{ t(designer ? 'approval.designer.trial.stagePick' : 'approval.submit.stagePick') }}</div>
      <div v-else-if="s.status === 'skip'" class="text-muted">{{ t('approval.submit.stageSkip') }}</div>
      <div v-else class="row items-center q-gutter-x-sm">
        <span>{{ joinList(s.approvers.map((a) => a.displayName), locale) }}</span>
        <q-badge v-if="s.usesFallback" outline color="warning" :label="t('approval.submit.stageFallback')" />
      </div>
      <div v-if="s.problem" class="text-negative q-mt-xs" :data-testid="`preview-problem-${s.code ?? i}`">{{ t(`approval.designer.trial.problem.${s.problem}`) }}</div>
    </q-timeline-entry>
  </q-timeline>
</template>
