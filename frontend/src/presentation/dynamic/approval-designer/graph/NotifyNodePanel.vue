<script setup lang="ts">
import { useI18n } from 'vue-i18n';

// 通知節點：尚未開放，只顯示說明。
const { t } = useI18n();
</script>

<template>
  <div class="text-muted" data-testid="notify-panel">{{ t('approval.designer.graph.notifyNotYet') }}</div>
</template>
