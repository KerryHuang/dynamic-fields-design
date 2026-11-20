<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useClearLocaleOverrides, useInstalledLocaleAdmin, useRemoveLocale, useRollbackLocalePack,
} from 'src/application/dynamic/queries/locale-packs-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import type { LocaleAdminEntry } from 'src/domain/dynamic/locale-pack-types';
import { formatTime } from 'src/domain/ops/ops-format';
import LocalePackImportDialog from 'src/presentation/locale/LocalePackImportDialog.vue';
import { notifyApiError } from 'src/presentation/dynamic/designer/api-error';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 最小語系頁（語系包設計 §6、§11；i4d1）：清單、匯入語系包，列尾「⋯」放回到上一版、移除覆寫、移除語系。權限與規則都在後端，這裡只做呈現。
const { t } = useI18n();
const $q = useQuasar();
const admin = useInstalledLocaleAdmin();
const rollback = useRollbackLocalePack();
const clear = useClearLocaleOverrides();
const remove = useRemoveLocale();

const forbidden = computed(() => isForbidden(admin.error.value));
const importEnabled = computed(() => admin.data.value?.importEnabled ?? false);
const showImport = ref(false);

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

const versionText = (l: LocaleAdminEntry) => {
  const c = l.current;
  if (!c) return '';
  const time = formatTime(c.importedUtc);
  const version = c.packVersion ?? c.fileName;
  return version ? t('localePacks.currentVersion', { version, time }) : t('localePacks.currentEmpty', { time });
};
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-mb-md q-gutter-sm">
        <div class="text-h6">{{ t('localePacks.title') }}</div>
        <q-space />
        <q-btn outline no-caps icon="upload" :label="t('localePacks.import')" :disable="!admin.data.value" data-testid="locales-import" @click="showImport = true" />
      </div>
      <p class="text-muted">{{ t('localePacks.publicNotice') }}</p>
      <div v-if="admin.data.value && !importEnabled" class="text-warning q-mb-md" data-testid="locales-import-disabled">{{ t('localePacks.importDisabled') }}</div>

      <q-list v-if="admin.data.value" bordered separator>
        <q-item v-for="l in admin.data.value.locales" :key="l.code" :data-testid="`locale-row-${l.code}`">
          <q-item-section>
            <q-item-label style="overflow-wrap: anywhere">
              <span class="text-weight-bold">{{ l.nativeName }}</span>
              <span class="text-caption text-muted q-ml-sm">{{ l.code }}</span>
              <q-badge outline class="q-ml-sm">{{ t(`localePacks.origin.${l.origin}`) }}</q-badge>
              <q-badge v-if="l.enabled" color="positive" class="q-ml-xs">{{ t('localePacks.enabled') }}</q-badge>
              <q-badge v-if="l.isDefault" color="primary" class="q-ml-xs">{{ t('localePacks.default') }}</q-badge>
            </q-item-label>
            <q-item-label v-if="l.overrides.ui + l.overrides.server > 0" caption>
              {{ t('localePacks.overrides', { ui: l.overrides.ui, server: l.overrides.server }) }}
            </q-item-label>
            <q-item-label v-if="l.current" caption :data-testid="`locale-version-${l.code}`">{{ versionText(l) }}</q-item-label>
          </q-item-section>
          <q-item-section side>
            <q-btn flat round dense icon="more_horiz" :aria-label="t('common.moreActions')" :data-testid="`locale-actions-${l.code}`">
              <q-menu>
                <q-list dense style="min-width: 200px">
                  <q-item v-if="l.hasPrevious" v-close-popup clickable :disable="!importEnabled" :data-testid="`locale-rollback-${l.code}`" @click="pending = { action: 'rollback', locale: l }">
                    <q-item-section>{{ t('localePacks.actions.rollback') }}</q-item-section>
                  </q-item>
                  <q-item v-if="l.overrides.ui + l.overrides.server > 0" v-close-popup clickable :disable="!importEnabled" :data-testid="`locale-clear-${l.code}`" @click="pending = { action: 'clear', locale: l }">
                    <q-item-section>{{ t('localePacks.actions.clear') }}</q-item-section>
                  </q-item>
                  <q-item v-if="l.origin === 'installed'" v-close-popup clickable :disable="!importEnabled" :data-testid="`locale-remove-${l.code}`" @click="pending = { action: 'remove', locale: l }">
                    <q-item-section>{{ t('localePacks.actions.remove') }}</q-item-section>
                  </q-item>
                  <q-item v-if="!importEnabled" dense>
                    <q-item-section class="text-caption text-muted">{{ t('localePacks.importDisabledItem') }}</q-item-section>
                  </q-item>
                </q-list>
              </q-menu>
            </q-btn>
          </q-item-section>
        </q-item>
      </q-list>

      <LocalePackImportDialog v-if="admin.data.value" v-model="showImport" :import-enabled="importEnabled" :pack-max-bytes="admin.data.value.packMaxBytes" />

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
