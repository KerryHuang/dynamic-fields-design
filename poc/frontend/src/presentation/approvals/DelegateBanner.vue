<script setup lang="ts">
import { date } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMyApprovalDelegate } from 'src/application/dynamic/queries/approval-delegate-queries';

// Design 13.4: who handles my approvals now, or from when; nothing once the period is over.
const { t } = useI18n();
const current = useMyApprovalDelegate();
const text = computed(() => {
  const d = current.data.value;
  if (!d) return '';
  const p = { name: d.delegateName || d.delegateUserId, start: d.startDate, end: d.endDate };
  if (d.active) return t('approval.delegate.bannerActive', p);
  return d.startDate > date.formatDate(new Date(), 'YYYY-MM-DD') ? t('approval.delegate.bannerUpcoming', p) : '';
});
</script>

<template>
  <q-banner v-if="text" dense rounded class="bg-info-soft q-mb-md delegate-banner" data-testid="delegate-banner">
    <template #avatar><q-icon name="swap_horiz" color="info" /></template>
    {{ text }}
  </q-banner>
</template>

<style scoped>
.delegate-banner { overflow-wrap: anywhere; }
</style>
