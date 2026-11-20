<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useClearLocaleOverrides, useInstalledLocaleAdmin, useRemoveLocale, useRollbackLocalePack,
} from 'src/application/dynamic/queries/locale-packs-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { LocaleAdminEntry } from 'src/domain/dynamic/locale-pack-types';
import { previewLocale } from 'src/i18n/locale-preview';
import { switchLocale } from 'src/i18n/ui-messages';
import LocaleCard from 'src/presentation/locale/LocaleCard.vue';
import LocalePackImportDialog from 'src/presentation/locale/LocalePackImportDialog.vue';
import LocaleTemplateExportDialog from 'src/presentation/locale/LocaleTemplateExportDialog.vue';
import { leftoverMessages, notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 語系頁（語系包設計 §6、§11；i4d1、i4d2）：每個語系一張卡片（完整度、覆寫檢視、匯出樣板、以此語系預覽、「⋯」放回到上一版、移除覆寫、移除語系）。
// 權限與規則都在後端，這裡只做呈現。
const composer = useI18n({ useScope: 'global' });
const { t } = composer;
const $q = useQuasar();
const admin = useInstalledLocaleAdmin();
const rollback = useRollbackLocalePack();
const clear = useClearLocaleOverrides();
const remove = useRemoveLocale();

const forbidden = computed(() => isForbidden(admin.error.value));
// 首次清單失敗（非 403）顯示後端訊息並可重試；已有資料時背景重抓失敗不蓋掉清單
const loadError = computed(() => {
  const e = admin.error.value as unknown as ApiError | null;
  if (!e || forbidden.value || admin.data.value) return null;
  return leftoverMessages(e, []).join('；') || t('dynamic.loadFailed');
});
const importEnabled = computed(() => admin.data.value?.importEnabled ?? false);
const showImport = ref(false);
const showExport = ref(false);
const exportTarget = ref<LocaleAdminEntry | null>(null);
function openExport(target: LocaleAdminEntry | null) {
  exportTarget.value = target;
  showExport.value = true;
}

// 管理員試看（M2）：沿用 switchLocale 的完整試看流程；previewLocale 不是響應式，切完自己記一份讓按鈕跟著換
const previewing = ref<string | null>(previewLocale());
async function startPreview(code: string) {
  await switchLocale({ global: composer }, code, { preview: true });
  previewing.value = previewLocale();
}

type Action = 'rollback' | 'clear' | 'remove';
const pending = ref<{ action: Action; locale: LocaleAdminEntry } | null>(null);

const confirmText = computed(() => {
  const p = pending.value;
  if (!p) return '';
  const name = p.locale.nativeName;
  if (p.action === 'rollback') return t('localePacks.confirm.rollback');
  if (p.action === 'remove') return t('localePacks.confirm.remove', { name });
  return t(p.locale.origin === 'builtin' ? 'localePacks.confirm.clearBuiltin' : 'localePacks.confirm.clearInstalled', { name });
});

const busy = computed(() => rollback.isPending.value || clear.isPending.value || remove.isPending.value);

function confirmRun() {
  const p = pending.value;
  if (!p) return;
  const mutation = { rollback, clear, remove }[p.action];
  mutation.mutate(p.locale.code, {
    onSuccess: () => {
      $q.notify({ type: 'positive', message: t(`localePacks.done.${p.action}`) });
      pending.value = null;
    },
    onError: (e) => {
      notifyApiError((o) => $q.notify(o), e as unknown as ApiError, [], t('localePacks.failed'));
      pending.value = null;
    },
  });
}

</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md q-gutter-sm">
        <div class="text-h6">{{ t('localePacks.title') }}</div>
        <q-space />
        <q-btn outline no-caps icon="note_add" :label="t('localePacks.newTemplate')" :disable="!admin.data.value" data-testid="locales-new-template" @click="openExport(null)" />
        <q-btn outline no-caps icon="upload" :label="t('localePacks.import')" :disable="!admin.data.value" data-testid="locales-import" @click="showImport = true" />
      </div>
      <p class="text-muted">{{ t('localePacks.publicNotice') }}</p>
      <div v-if="loadError" class="text-negative q-mb-md" style="overflow-wrap: anywhere" data-testid="locales-load-error">
        {{ loadError }}
        <q-btn flat dense no-caps color="primary" class="q-ml-sm" :label="t('dynamic.reload')" :loading="admin.isFetching.value"
          data-testid="locales-retry" @click="admin.refetch()" />
      </div>
      <div v-if="admin.data.value && !importEnabled" class="text-warning q-mb-md" data-testid="locales-import-disabled">{{ t('localePacks.importDisabled') }}</div>

      <div v-if="admin.data.value" class="row q-col-gutter-md">
        <div v-for="l in admin.data.value.locales" :key="l.code" class="col-12 col-sm-6 col-md-4">
          <LocaleCard :locale="l" :import-enabled="importEnabled" :previewing="previewing"
            @export-template="openExport(l)" @preview="startPreview(l.code)" @action="(a) => (pending = { action: a, locale: l })" />
        </div>
      </div>

      <LocalePackImportDialog v-if="admin.data.value" v-model="showImport" :import-enabled="importEnabled" :pack-max-bytes="admin.data.value.packMaxBytes" />
      <LocaleTemplateExportDialog v-if="admin.data.value" v-model="showExport" :target="exportTarget" :locales="admin.data.value.locales"
        :pack-max-bytes="admin.data.value.packMaxBytes" />

      <q-dialog :model-value="!!pending" @update:model-value="pending = null">
        <q-card style="width: 420px; max-width: 100%" data-testid="locale-confirm">
          <q-card-section>{{ confirmText }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps :label="t('dynamic.cancel')" @click="pending = null" />
            <q-btn color="negative" no-caps :label="t('localePacks.confirm.ok')" :loading="busy" data-testid="locale-confirm-ok" @click="confirmRun" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
