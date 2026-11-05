<script setup lang="ts">
import { useI18n } from 'vue-i18n';

const { t } = useI18n();
</script>

<template>
  <q-item><q-item-section class="text-grey">{{ t('select.noMatch') }}</q-item-section></q-item>
</template>
