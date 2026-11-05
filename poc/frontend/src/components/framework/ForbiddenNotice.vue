<script setup lang="ts">
import { useI18n } from 'vue-i18n';

const { t } = useI18n();
</script>

<template>
  <div class="text-negative" data-testid="forbidden">{{ t('dynamic.forbidden') }}</div>
</template>
