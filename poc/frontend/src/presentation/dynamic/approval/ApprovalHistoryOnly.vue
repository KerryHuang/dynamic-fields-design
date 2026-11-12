<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { RecordApproval } from 'src/domain/dynamic/approval-types';
import { instanceTitle } from './approval-display';
import ApprovalPanel from './ApprovalPanel.vue';

/** What an approver sees once the record itself is out of reach: only the approval history, no record content.
 *  The bar stays for what needs no read right: the initiator may still withdraw (the server's canWithdraw decides). */
const props = defineProps<{ approval: RecordApproval; recordId: number; moduleKey: string }>();
const { t, locale } = useI18n();
const main = computed(() => props.approval.current ?? props.approval.previous[0]);
const title = computed(() => instanceTitle(main.value?.titleSnapshot ?? '', undefined, props.recordId, locale.value));
</script>

<template>
  <div data-testid="approval-history-only">
    <q-btn flat no-caps dense icon="arrow_back" :label="t('approval.historyOnly.backToCenter')" to="/approvals" class="q-mb-sm" data-testid="approval-history-back" />
    <div class="text-h6 approval-title" data-testid="approval-history-title">{{ title }}</div>
    <div class="row items-center no-wrap q-mb-md text-muted">
      <q-icon name="history" class="q-mr-sm" />
      <span>{{ t('approval.historyOnly.hint') }}</span>
    </div>
    <ApprovalPanel :approval="approval" :schema="null" :record-id="recordId" :module-key="moduleKey" />
  </div>
</template>

<style scoped>
.approval-title { overflow-wrap: anywhere; }
</style>
