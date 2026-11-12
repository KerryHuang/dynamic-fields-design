<script setup lang="ts">
import { date } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { ApprovalInstanceView, RecordApproval } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import ApprovalActionBar from './ApprovalActionBar.vue';
import ApprovalActionLog from './ApprovalActionLog.vue';
import ApprovalStageList from './ApprovalStageList.vue';

const props = defineProps<{ approval: RecordApproval; schema: ModuleSchema | null; readonly?: boolean; recordId?: number; moduleKey?: string }>();
const { t, locale } = useI18n();
const timeText = (iso: string) => date.formatDate(new Date(iso), 'YYYY-MM-DD HH:mm');
const heading = (i: ApprovalInstanceView) => (i.flowName ? resolveI18n(i.flowName, locale.value) : t('approval.panel.fallbackRoute'));

// The running instance is the main block; once nothing is running, the latest ended one is, so the approver who just
// approved (or the initiator who was returned) still sees the timeline and the reason. Older ones stay folded.
const main = computed(() => props.approval.current ?? props.approval.previous[0] ?? null);
const earlier = computed(() => (props.approval.current ? props.approval.previous : props.approval.previous.slice(1)));
const MAIN_COLOR: Record<string, string> = { pending: 'primary', approved: 'positive', rejected: 'negative' };
const mainColor = (i: ApprovalInstanceView) => MAIN_COLOR[i.status] ?? 'muted';
// In the folded list a rerouted instance has a successor, so "please submit again" would mislead there.
const earlierStatus = (i: ApprovalInstanceView) => (i.status === 'rerouted' ? t('approval.panel.rerouted') : t(`approval.status.${i.status}`));
</script>

<template>
  <q-card v-if="main" flat bordered class="q-mb-md approval-panel" data-testid="approval-panel">
    <q-card-section class="row items-center q-gutter-sm">
      <div class="text-subtitle1">{{ t('approval.panel.title') }}</div>
      <q-badge outline :color="mainColor(main)" :label="t(`approval.status.${main.status}`)" data-testid="approval-main-status" />
      <span class="text-muted approval-wrap">{{ heading(main) }}</span>
      <q-space />
      <ApprovalActionBar v-if="!readonly && recordId !== undefined && moduleKey" :instance="main" :record-id="recordId" :module-key="moduleKey" />
    </q-card-section>
    <q-card-section class="q-pt-none">
      <div class="text-caption text-muted">{{ t('approval.panel.submitted', { time: timeText(main.submittedTime) }) }}</div>
      <ApprovalStageList :stages="main.stages" />
      <ApprovalActionLog :actions="main.actions" :schema="schema" />
    </q-card-section>
    <q-expansion-item
      v-if="earlier.length" :label="t('approval.panel.previous')" :caption="String(earlier.length)" dense-toggle
      data-testid="approval-previous"
    >
      <q-card-section v-for="p in earlier" :key="p.id" :data-testid="`previous-${p.id}`">
        <div class="row items-center q-gutter-sm q-mb-sm">
          <q-badge outline color="muted" :label="earlierStatus(p)" />
          <span class="text-muted approval-wrap">{{ heading(p) }} · {{ timeText(p.submittedTime) }}</span>
        </div>
        <ApprovalStageList :stages="p.stages" />
        <ApprovalActionLog :actions="p.actions" :schema="schema" />
      </q-card-section>
    </q-expansion-item>
  </q-card>
</template>

<style scoped>
.approval-wrap { overflow-wrap: anywhere; }
</style>
