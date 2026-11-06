<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { DeleteCheck, DeleteTargetRef } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';

/**
 * 模組、欄位、區塊的安全刪除：先預檢（擋下就列出原因與來源），可刪才顯示永久清除的警告與筆數，
 * 確認時把使用者看到的筆數一起送出；後端筆數對不上（又有人刪了記錄）會回 409，這裡換成新數字並要求重新確認。
 */
const props = defineProps<{ modelValue: boolean; target: DeleteTargetRef | null; name: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; deleted: [] }>();

const { t } = useI18n();
const $q = useQuasar();

const loading = ref(false);
const forbidden = ref(false);
const check = ref<DeleteCheck | null>(null);
const stale = ref(false);
const typedKey = ref('');
const acknowledged = ref(false);
const deleting = ref(false);

const moduleKey = computed(() => (props.target?.kind === 'module' ? props.target.key : null));
const confirmed = computed(() => (moduleKey.value !== null ? typedKey.value === moduleKey.value : acknowledged.value));
const canSubmit = computed(() => !!check.value?.canDelete && confirmed.value && !deleting.value);

function resetConfirmation() {
  typedKey.value = '';
  acknowledged.value = false;
}

async function load() {
  if (!props.target) return;
  loading.value = true;
  forbidden.value = false;
  check.value = null;
  stale.value = false;
  resetConfirmation();
  try {
    check.value = await dynamicApi.deleteCheck(props.target);
  } catch (e) {
    if (isForbidden(e)) forbidden.value = true;
    else $q.notify({ type: 'negative', message: (e as ApiError).message ?? t('designer.delete.checkFailed') });
  } finally {
    loading.value = false;
  }
}

watch(() => props.modelValue, (open) => { if (open) void load(); }, { immediate: true });

async function submit() {
  if (!props.target || !check.value || !canSubmit.value) return;
  deleting.value = true;
  try {
    await dynamicApi.deleteTarget(props.target, check.value.purge.records);
    $q.notify({ type: 'positive', message: t('designer.delete.done') });
    emit('deleted');
    emit('update:modelValue', false);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 409 && err.deleteCheck) {
      // 筆數變了或剛被別人引用：換成最新的檢查結果，已輸入的確認作廢
      stale.value = err.deleteCheck.canDelete;
      check.value = err.deleteCheck;
      resetConfirmation();
    } else if (isForbidden(e)) {
      forbidden.value = true;
    } else {
      $q.notify({ type: 'negative', message: err.message ?? t('designer.delete.failed') });
    }
  } finally {
    deleting.value = false;
  }
}

const blockerLine = (b: DeleteCheck['blockers'][number]) =>
  [t(`designer.delete.kind.${b.kind}`), b.moduleKey, b.name, b.count != null ? t('designer.delete.count', { n: b.count }) : null]
    .filter((x) => x != null && x !== '').join(' · ');
</script>

<template>
  <q-dialog :model-value="modelValue" persistent data-testid="delete-dialog" @update:model-value="emit('update:modelValue', $event)">
    <q-card style="min-width: 480px; max-width: 90vw">
      <q-card-section class="text-h6">{{ t('designer.delete.title', { name }) }}</q-card-section>

      <q-card-section v-if="loading" class="text-muted">{{ t('designer.delete.checking') }}</q-card-section>

      <q-card-section v-else-if="forbidden" class="text-negative" data-testid="delete-forbidden">
        {{ t('designer.delete.forbidden') }}
      </q-card-section>

      <q-card-section v-else-if="check && !check.canDelete">
        <div class="q-mb-sm">{{ t('designer.delete.blockedIntro') }}</div>
        <q-list dense bordered separator data-testid="delete-blockers">
          <q-item v-for="(b, i) in check.blockers" :key="`${b.kind}-${b.moduleKey}-${b.id}-${i}`">
            <q-item-section>{{ blockerLine(b) }}</q-item-section>
          </q-item>
        </q-list>
      </q-card-section>

      <q-card-section v-else-if="check" class="q-gutter-md">
        <div v-if="stale" class="text-negative" data-testid="delete-stale">{{ t('designer.delete.purgeChanged') }}</div>
        <div class="text-negative" data-testid="delete-warning">
          <div class="text-weight-bold">{{ t('designer.delete.warnTitle') }}</div>
          <div>
            {{ check.purge.records + check.purge.indexRows + check.purge.historyRows > 0
              ? t('designer.delete.warnPurge', check.purge)
              : t('designer.delete.warnNone') }}
          </div>
        </div>
        <q-input
          v-if="moduleKey !== null" v-model="typedKey" outlined dense autofocus data-testid="delete-confirm-input"
          :label="t('designer.delete.confirmKey', { key: moduleKey })"
        />
        <q-checkbox v-else v-model="acknowledged" :label="t('designer.delete.confirmCheck')" data-testid="delete-confirm-check" />
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat :label="t('designer.close')" :disable="deleting" data-testid="delete-close" @click="emit('update:modelValue', false)" />
        <q-btn
          v-if="check?.canDelete && !forbidden" color="negative" :label="t('designer.delete.submit')" :disable="!canSubmit"
          :loading="deleting" data-testid="delete-submit" @click="submit"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
