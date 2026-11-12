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
  <q-badge v-if="text" :color="color" class="q-pa-xs" data-testid="approval-badge">{{ text }}</q-badge>
</template>
