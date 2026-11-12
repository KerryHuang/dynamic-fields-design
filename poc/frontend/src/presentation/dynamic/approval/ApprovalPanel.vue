<script setup lang="ts">
import { date } from 'quasar';
import { useI18n } from 'vue-i18n';
import type { ApprovalInstanceView, RecordApproval } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ModuleSchema } from 'src/domain/dynamic/types';
import ApprovalActionLog from './ApprovalActionLog.vue';
import ApprovalStageList from './ApprovalStageList.vue';

defineProps<{ approval: RecordApproval; schema: ModuleSchema | null; readonly?: boolean }>();
const { t, locale } = useI18n();
const timeText = (iso: string) => date.formatDate(new Date(iso), 'YYYY-MM-DD HH:mm');
const heading = (i: ApprovalInstanceView) => (i.flowName ? resolveI18n(i.flowName, locale.value) : t('approval.panel.fallbackRoute'));
</script>

<template>
  <q-card v-if="approval.current || approval.previous.length" flat bordered class="q-mb-md approval-panel" data-testid="approval-panel">
    <template v-if="approval.current">
      <q-card-section class="row items-center q-gutter-sm">
        <div class="text-subtitle1">{{ t('approval.panel.title') }}</div>
        <q-badge outline color="primary" :label="t(`approval.status.${approval.current.status}`)" />
        <span class="text-muted">{{ heading(approval.current) }}</span>
        <q-space />
        <slot name="actions" :instance="approval.current" />
      </q-card-section>
      <q-card-section class="q-pt-none">
        <div class="text-caption text-muted">{{ t('approval.panel.submitted', { time: timeText(approval.current.submittedTime) }) }}</div>
        <ApprovalStageList :stages="approval.current.stages" />
        <ApprovalActionLog :actions="approval.current.actions" :schema="schema" />
      </q-card-section>
    </template>
    <q-expansion-item
      v-if="approval.previous.length" :label="t('approval.panel.previous')" :caption="String(approval.previous.length)" dense-toggle
      data-testid="approval-previous"
    >
      <q-card-section v-for="p in approval.previous" :key="p.id" :data-testid="`previous-${p.id}`">
        <div class="row items-center q-gutter-sm q-mb-sm">
          <q-badge outline color="muted" :label="t(`approval.status.${p.status}`)" />
          <span class="text-muted">{{ heading(p) }} · {{ timeText(p.submittedTime) }}</span>
        </div>
        <ApprovalStageList :stages="p.stages" />
        <ApprovalActionLog :actions="p.actions" :schema="schema" />
      </q-card-section>
    </q-expansion-item>
  </q-card>
</template>
