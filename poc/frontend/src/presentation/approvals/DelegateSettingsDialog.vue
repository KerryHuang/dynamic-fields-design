<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useClearMyApprovalDelegate, useMyApprovalDelegate, useSaveMyApprovalDelegate,
} from 'src/application/dynamic/queries/approval-delegate-queries';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import { joinList } from 'src/i18n/translate';
import type { ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

// The caller's own delegate (design 7.7, 13.5): who, from and to (business days, both ends included); or clear it.
const props = defineProps<{ modelValue: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
const { t } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();
const current = useMyApprovalDelegate();
const users = useUsers();
const save = useSaveMyApprovalDelegate();
const clear = useClearMyApprovalDelegate();

// The server's business day: the browser's date may already be another day (time zone, a page left open overnight).
const today = () => current.data.value?.today ?? '';
const delegateUserId = ref<string | null>(null);
const startDate = ref('');
const endDate = ref('');
const errors = ref<Record<string, string>>({});
const topError = ref('');

/** Every opening starts from the saved setting (or today to today when there is none). */
function reset() {
  const d = current.data.value?.delegate;
  delegateUserId.value = d?.delegateUserId ?? null;
  startDate.value = d?.startDate ?? today();
  endDate.value = d?.endDate ?? today();
  errors.value = {};
  topError.value = '';
}
watch(() => props.modelValue, (open) => { if (open) reset(); }, { immediate: true });
watch(() => current.data.value, () => { if (props.modelValue && !save.isPending.value) reset(); });

const candidates = computed(() => (users.data.value ?? [])
  .filter((u) => u.userId !== identity.userId)
  .map((u) => ({ value: u.userId, label: u.displayName || u.userId })));
const pending = computed(() => save.isPending.value || clear.isPending.value);
const close = () => emit('update:modelValue', false);

function failed(e: unknown) {
  const err = e as ApiError;
  if (err.status === 400 && err.errors) {
    const rest: string[] = [];
    for (const [k, v] of Object.entries(err.errors)) {
      if (k === 'delegateUserId' || k === 'startDate' || k === 'endDate') errors.value[k] = joinList(v);
      else rest.push(...v);
    }
    topError.value = joinList(rest);
  } else topError.value = err.message ?? t('designer.saveFailed');
}

async function send() {
  errors.value = {};
  topError.value = '';
  if (!delegateUserId.value) errors.value.delegateUserId = t('approval.delegate.userRequired');
  if (!startDate.value) errors.value.startDate = t('approval.delegate.dateRequired');
  if (!endDate.value) errors.value.endDate = t('approval.delegate.dateRequired');
  // Same-length yyyy-MM-dd strings compare as dates
  if (startDate.value && endDate.value && startDate.value > endDate.value) errors.value.startDate = t('approval.delegate.periodInvalid');
  if (Object.keys(errors.value).length) return;
  try {
    await save.mutateAsync({ delegateUserId: delegateUserId.value!, startDate: startDate.value, endDate: endDate.value });
    $q.notify({ type: 'positive', message: t('approval.delegate.saved') });
    close();
  } catch (e) {
    failed(e);
  }
}

async function remove() {
  topError.value = '';
  try {
    await clear.mutateAsync();
    $q.notify({ type: 'positive', message: t('approval.delegate.cleared') });
    close();
  } catch (e) {
    failed(e);
  }
}
</script>

<template>
  <q-dialog :model-value="modelValue" persistent :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card class="column no-wrap" :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw; max-height: 90vh'" data-testid="delegate-dialog">
      <q-card-section class="text-h6">{{ t('approval.delegate.title') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none">
        <div class="q-mb-md delegate-wrap">{{ t('approval.delegate.hint') }}</div>
        <div class="q-mb-md" data-testid="delegate-user">
          <GenericSelect v-model="delegateUserId" :label="t('approval.delegate.user')" :options="candidates" :error="errors.delegateUserId" required />
        </div>
        <div class="row q-col-gutter-sm">
          <div class="col-12 col-sm-6">
            <q-input
              v-model="startDate" type="date" stack-label outlined :label="t('approval.delegate.start')" :error="!!errors.startDate"
              :error-message="errors.startDate" data-testid="delegate-start"
            />
          </div>
          <div class="col-12 col-sm-6">
            <q-input
              v-model="endDate" type="date" stack-label outlined :label="t('approval.delegate.end')" :error="!!errors.endDate"
              :error-message="errors.endDate" data-testid="delegate-end"
            />
          </div>
        </div>
        <div v-if="topError" class="text-negative q-mt-md delegate-wrap" data-testid="delegate-error">{{ topError }}</div>
      </q-card-section>
      <q-card-actions align="right" class="q-gutter-sm">
        <q-btn
          v-if="current.data.value?.delegate" flat color="negative" :label="t('approval.delegate.clear')" :loading="clear.isPending.value" :disable="pending"
          data-testid="delegate-clear" @click="remove"
        />
        <q-space />
        <q-btn flat :label="t('dynamic.cancel')" :disable="pending" @click="close" />
        <q-btn color="primary" :label="t('approval.delegate.save')" :loading="save.isPending.value" :disable="pending" data-testid="delegate-save" @click="send" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.delegate-wrap { overflow-wrap: anywhere; }
</style>
