<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { RecordApprovalSummary } from 'src/domain/dynamic/approval-types';
import { badgeColor, badgeText } from './approval-display';

const props = defineProps<{ summary: RecordApprovalSummary }>();
const { t, locale } = useI18n();
const text = computed(() => badgeText(props.summary, locale.value, t));
const color = computed(() => badgeColor(props.summary.status));
</script>

<template>
  <q-badge v-if="text" :color="color" class="q-pa-xs approval-badge" data-testid="approval-badge">{{ text }}</q-badge>
</template>

<style scoped>
/* Stage names are free text: let the badge wrap instead of widening the page. */
.approval-badge { white-space: normal; max-width: 100%; }
</style>
