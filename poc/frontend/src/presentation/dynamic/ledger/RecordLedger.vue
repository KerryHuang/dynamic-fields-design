<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useRecordLedger, useReverseRecord } from 'src/application/dynamic/queries/ledger-queries';
import { messagesOutside } from 'src/presentation/dynamic/designer/api-error';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import type { ApiError } from 'src/services/api/http';

const props = defineProps<{ recordId: number; moduleKey: string }>();
const { t } = useI18n();
const $q = useQuasar();
const router = useRouter();

const ledger = useRecordLedger(() => props.recordId);
const reverse = useReverseRecord();
const confirming = ref(false);
// Kept across a failed attempt so a resend takes effect once; replaced after a success or a 422 (key used for other content).
let idempotencyKey = newIdempotencyKey();

const info = computed(() => ledger.data.value);
// A reversing entry is itself never reversed, whatever the server says about a stale cache.
const canReverse = computed(() => !!info.value?.reversible && !info.value.reverses);
const link = (r: { id: number; title: string }) => ({ to: `/dynamic/${props.moduleKey}/${r.id}`, text: r.title || `#${r.id}` });

async function submit() {
  try {
    const rec = await reverse.mutateAsync({ id: props.recordId, idempotencyKey });
    idempotencyKey = newIdempotencyKey();
    confirming.value = false;
    $q.notify({ type: 'positive', message: t('ledger.reverse.done') });
    await router.push(`/dynamic/${rec.moduleKey}/${rec.id}`);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 422) idempotencyKey = newIdempotencyKey();
    // a network failure or a rejected input leaves the dialog open for another try; a conflict means the entry changed, so show its real state
    if (err.status === 409 || err.status === 403 || err.status === 404) {
      confirming.value = false;
      void ledger.refetch();
    }
    const lines = err.status === 400 ? messagesOutside(err.errors, []) : [];
    $q.notify({ type: 'negative', message: (lines.length ? lines.join('、') : err.message) ?? t('designer.saveFailed') });
  }
}
</script>

<template>
  <div class="q-mb-md q-gutter-y-sm" data-testid="record-ledger">
    <div class="row items-center no-wrap text-muted">
      <q-icon name="lock" class="q-mr-sm" />
      <span>{{ t('ledger.entryNotice') }}</span>
    </div>
    <div v-if="info?.reverses" data-testid="ledger-reverses">
      <span class="text-muted">{{ t('ledger.reverse.reverses') }}：</span>
      <router-link :to="link(info.reverses).to">{{ link(info.reverses).text }}</router-link>
    </div>
    <div v-if="info?.reversedBy" data-testid="ledger-reversed-by">
      <span class="text-muted">{{ t('ledger.reverse.reversedBy') }}：</span>
      <router-link :to="link(info.reversedBy).to">{{ link(info.reversedBy).text }}</router-link>
    </div>
    <q-btn v-if="canReverse" outline color="primary" icon="undo" :label="t('ledger.reverse.button')" data-testid="reverse-open" @click="confirming = true" />

    <q-dialog v-model="confirming">
      <q-card class="record-ledger__dialog" data-testid="reverse-dialog">
        <q-card-section class="text-h6">{{ t('ledger.reverse.title') }}</q-card-section>
        <q-card-section>{{ t('ledger.reverse.confirm') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat :label="t('dynamic.cancel')" data-testid="reverse-cancel" />
          <q-btn color="primary" :label="t('ledger.reverse.ok')" :loading="reverse.isPending.value" data-testid="reverse-confirm" @click="submit" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.record-ledger__dialog { width: 440px; max-width: 95vw; }
</style>
