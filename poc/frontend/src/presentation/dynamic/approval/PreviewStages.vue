<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { PreviewStage } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { joinList } from 'src/i18n/translate';

/** `designer`: the designer's trial reads the pick stage as the initiator's later choice, not the viewer's. */
const props = defineProps<{ stages: PreviewStage[]; designer?: boolean }>();
const { t, locale } = useI18n();

/** The first item of an any-join group gets a heading above it; every item of the group carries a badge. Sequence previews have neither. */
const startsAnyGroup = (i: number) => {
  const group = props.stages[i]?.anyGroup;
  return !!group && props.stages.findIndex((s) => s.anyGroup === group) === i;
};

/**
 * Groups are told apart by order of first appearance (badge colour). A nested group sits inside the span of its outer group
 * (the outer group has items both before and after it), so its depth is the number of such enclosing groups; the heading is indented by it.
 */
const groups = computed(() => {
  const span = new Map<string, [number, number]>();
  props.stages.forEach((s, i) => { if (s.anyGroup) span.set(s.anyGroup, [span.get(s.anyGroup)?.[0] ?? i, i]); });
  return [...span.entries()].map(([code, [first, last]], n) => ({
    code, color: ['primary', 'deep-orange', 'teal', 'purple'][n % 4],
    depth: [...span.entries()].filter(([c, [f, l]]) => c !== code && f < first && l > last).length,
  }));
});
const groupOf = (code: string | null | undefined) => groups.value.find((g) => g.code === code);
</script>

<template>
  <q-timeline color="primary" class="q-pl-sm" data-testid="preview-stages">
    <template v-for="(s, i) in stages" :key="s.code ?? i">
      <q-timeline-entry
        v-if="startsAnyGroup(i)" heading :data-testid="`preview-any-group-${s.anyGroup}`"
        :style="{ paddingLeft: `${(groupOf(s.anyGroup)?.depth ?? 0) * 16}px` }"
      >{{ t('approval.submit.stageAnyGroup') }}</q-timeline-entry>
      <q-timeline-entry
        :title="resolveI18n(s.name, locale)" :data-testid="`preview-stage-${s.code ?? i}`"
        :icon="s.status === 'pick' ? 'how_to_reg' : s.status === 'conditional' ? 'rule' : 'person'"
        :color="groupOf(s.anyGroup)?.color"
      >
        <q-badge
          v-if="s.anyGroup" outline class="q-mb-xs" :color="groupOf(s.anyGroup)?.color" :label="t('approval.submit.stageAnyBranch')"
          :data-testid="`preview-any-badge-${s.code ?? i}`"
        />
        <div v-if="s.status === 'conditional'" class="text-muted">{{ t('approval.submit.stageConditional') }}</div>
        <div v-else-if="s.status === 'pick'" class="text-muted">{{ t(designer ? 'approval.designer.trial.stagePick' : 'approval.submit.stagePick') }}</div>
        <div v-else-if="s.status === 'skip'" class="text-muted">{{ t('approval.submit.stageSkip') }}</div>
        <div v-else class="row items-center q-gutter-x-sm">
          <span>{{ joinList(s.approvers.map((a) => a.displayName), locale) }}</span>
          <q-badge v-if="s.usesFallback" outline color="warning" :label="t('approval.submit.stageFallback')" />
        </div>
        <div v-if="s.problem" class="text-negative q-mt-xs" :data-testid="`preview-problem-${s.code ?? i}`">{{ t(`approval.designer.trial.problem.${s.problem}`) }}</div>
      </q-timeline-entry>
    </template>
  </q-timeline>
</template>
