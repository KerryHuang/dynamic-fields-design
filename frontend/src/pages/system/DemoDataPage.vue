<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDemoDataStatus, useGenerateDemoData } from 'src/application/dynamic/queries/demo-data-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t, locale } = useI18n();
const $q = useQuasar();
const status = useDemoDataStatus();
const generate = useGenerateDemoData();
const me = useMe();

const data = computed(() => status.data.value);
const forbidden = computed(() => isForbidden(status.error.value));
// 產生要重播設計器的新增與修改指令，缺任一項都會 403
const canGenerate = computed(() => !!me.data.value?.designer.add && !!me.data.value?.designer.edit);
const running = computed(() => data.value?.state === 'running');
const disabled = computed(() =>
  !canGenerate.value || !data.value || ['running', 'generated', 'conflict'].includes(data.value.state));

// total 剛開始可能是 0：只顯示「產生中」
const stateLabel = computed(() => {
  const d = data.value;
  if (!d) return '';
  if (d.state === 'running' && d.total === 0) return t('demoData.state.runningNoTotal');
  return t(`demoData.state.${d.state}`, { done: d.done, total: d.total });
});
// 由資料庫推得的 generated 沒有時間欄位
const finishedAt = computed(() => (data.value?.finishedAt ? new Date(data.value.finishedAt).toLocaleString(locale.value) : ''));

const start = () => generate.mutate(undefined, {
  onSuccess: () => $q.notify({ type: 'positive', message: t('demoData.started') }),
  onError: (e) => notifyApiError((o) => $q.notify(o), e as unknown as ApiError, [], t('demoData.startFailed')),
});
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="text-h6 q-mb-md">{{ t('demoData.title') }}</div>
      <p>{{ t('demoData.description') }}</p>
      <div v-if="data" class="q-mb-md">
        <div class="text-subtitle1" data-testid="demo-state">{{ stateLabel }}</div>
        <div v-if="data.state === 'generated' && finishedAt" class="text-caption" data-testid="demo-finished">
          {{ t('demoData.finishedAt') }}：{{ finishedAt }}
        </div>
        <div v-if="data.state === 'failed' && data.message" class="text-negative" data-testid="demo-message">{{ data.message }}</div>
        <template v-if="data.state === 'conflict'">
          <ul data-testid="demo-conflicts">
            <li v-for="c in data.conflicts" :key="c">{{ c }}</li>
          </ul>
          <div class="text-caption" data-testid="demo-conflict-hint">{{ t('demoData.conflictHint') }}</div>
        </template>
      </div>
      <q-btn
        color="primary" icon="auto_awesome" :label="t('demoData.generate')"
        :disable="disabled || generate.isPending.value" :loading="running || generate.isPending.value"
        data-testid="demo-generate" @click="start"
      />
    </template>
  </q-page>
</template>
