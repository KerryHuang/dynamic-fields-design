<script setup lang="ts">
import { Notify } from 'quasar';
import { ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useReportShareTargets, useSetReportShares } from 'src/application/dynamic/queries/reports-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ reportId: number; open: boolean; groupIds: number[] }>();
const emit = defineEmits<{ close: [] }>();
const { t, locale } = useI18n();
const groups = useReportShareTargets();
const save = useSetReportShares();
const selected = ref<number[]>([]);
const error = ref('');
// 每次開啟都從目前的分享狀態起算
watch(() => props.open, (o) => { if (o) { selected.value = [...props.groupIds]; error.value = ''; } }, { immediate: true });

function submit() {
  error.value = '';
  save.mutate({ id: props.reportId, groupIds: selected.value }, {
    onSuccess: () => { Notify.create({ type: 'positive', message: t('reports.shareSaved') }); emit('close'); },
    onError: (e) => { error.value = leftoverMessages(e as unknown as ApiError, []).join('、') || t('reports.saveFailed'); },
  });
}
</script>

<template>
  <q-dialog :model-value="open" @update:model-value="emit('close')">
    <q-card style="width: 480px; max-width: 95vw">
      <q-card-section class="text-h6">{{ t('reports.shareTitle') }}</q-card-section>
      <q-card-section class="column">
        <div v-if="groups.isError.value" class="text-negative" data-testid="share-load-error">
          {{ t('reports.shareLoadFailed') }}
          <q-btn flat dense no-caps color="primary" :label="t('reports.shareRetry')" data-testid="share-retry" @click="groups.refetch()" />
        </div>
        <q-checkbox
          v-for="g in groups.data.value ?? []" :key="g.id" v-model="selected" :val="g.id" dense
          :label="resolveI18n(g.name, locale)" :data-testid="`share-group-${g.id}`"
        />
        <div v-if="groups.data.value && groups.data.value.length === 0" class="text-grey-7">{{ t('reports.noGroups') }}</div>
        <div v-if="error" class="text-negative q-mt-sm" data-testid="share-error">{{ error }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('reports.cancel')" data-testid="share-cancel" @click="emit('close')" />
        <q-btn color="primary" :label="t('reports.save')" :loading="save.isPending.value" :disable="!groups.data.value" data-testid="share-save" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
