<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { safeRedirect } from 'src/router/auth-guard';

const { t } = useI18n();
const route = useRoute();
const router = useRouter();

// Navigating re-runs the auth guard, which probes GET /me again.
const retry = () => {
  const redirect = route.query.redirect;
  void router.replace(safeRedirect(typeof redirect === 'string' ? redirect : undefined));
};
</script>

<template>
  <div class="column items-center q-pa-md q-gutter-md">
    <div class="text-h6" data-testid="service-unavailable">{{ t('auth.serviceUnavailable') }}</div>
    <q-btn color="primary" :label="t('auth.retry')" data-testid="retry" @click="retry" />
  </div>
</template>
