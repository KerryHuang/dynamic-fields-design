<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useHealthChecks } from 'src/application/dynamic/queries/ops-queries';
import HealthCheckCard from './HealthCheckCard.vue';

// 健康檢查分頁（維運設計 §17.3 第 3 項）：筆電兩欄、桌機三欄。對這個資料庫種類不適用的檢查不列（例：PostgreSQL 的 WAL 檔），
// Demo 站關掉的磁碟檢查照列並標「不適用（Demo 站）」（使用者裁決 2026-11-19）。
const { t } = useI18n();
const checks = useHealthChecks();
const shown = computed(() => (checks.data.value ?? []).filter((c) => c.applicable || c.state.messageCode === 'demoSite'));
</script>

<template>
  <div v-if="checks.isError.value" class="text-negative">{{ t('ops.loadFailed') }}</div>
  <div v-else-if="!checks.data.value" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else class="row q-col-gutter-sm" data-testid="health-cards">
    <div v-for="c in shown" :key="c.code" class="col-12 col-md-6 col-xl-4">
      <HealthCheckCard :check="c" />
    </div>
  </div>
</template>
