<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMailSettings, useSaveMailSettings, useTestMailSettings } from 'src/application/dynamic/queries/ops-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { mailTestError } from 'src/domain/ops/ops-format';
import type { MailSettingsInput, SmtpSecurity } from 'src/domain/ops/ops-types';
import { joinList } from 'src/i18n/translate';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 寄信設定（使用者裁決 2026-11-19）：帳號信與告警信共用的 SMTP 連線，全系統一份。
// 密碼：顯示「已設定」；留空＝不送 password 鍵（保留舊密碼）；按「清除密碼」才送 ""。Demo 站整頁唯讀。
const { t, te } = useI18n();
const $q = useQuasar();
const settings = useMailSettings();
const m = computed(() => settings.data.value);
const forbidden = computed(() => isForbidden(settings.error.value));
const readonly = computed(() => !m.value?.editable);

const SECURITIES: SmtpSecurity[] = ['startTls', 'sslOnConnect', 'none'];
const securityOptions = computed(() => SECURITIES.map((s) => ({ value: s, label: t(`ops.mail.security.${s}`) })));

const draft = ref({ host: '', port: '', security: 'startTls' as SmtpSecurity, userName: '', fromAddress: '', fromName: '' });
const password = ref('');
const clearPassword = ref(false);
watch(m, (v) => {
  if (!v) return;
  draft.value = {
    host: v.host ?? '', port: String(v.port), security: v.security, userName: v.userName ?? '', fromAddress: v.fromAddress ?? '', fromName: v.fromName ?? '',
  };
  password.value = '';
  clearPassword.value = false;
}, { immediate: true });

const passwordHint = computed(() => {
  if (clearPassword.value) return t('ops.mail.passwordWillClear');
  if (m.value?.passwordUnreadable) return t('ops.mail.passwordUnreadable');
  return m.value?.hasPassword ? t('ops.mail.passwordSet') : t('ops.mail.passwordNone');
});

/** 組 PUT 主體（不含密碼）。 */
function body(): Omit<MailSettingsInput, 'password'> {
  const d = draft.value;
  return {
    host: d.host.trim(), port: Number(d.port), security: d.security, fromAddress: d.fromAddress.trim(),
    ...(d.userName.trim() ? { userName: d.userName.trim() } : {}), ...(d.fromName.trim() ? { fromName: d.fromName.trim() } : {}),
  };
}

/** 密碼另外併進請求（不進共用快取）：留空不送鍵、按了清除送 ""、有填送新值。 */
function secret(): Pick<MailSettingsInput, 'password'> {
  if (password.value) return { password: password.value };
  return clearPassword.value ? { password: '' } : {};
}

const errors = ref<Record<string, string[]>>({});
const err = (k: string) => (errors.value[k] ?? []).join('；');
const save = useSaveMailSettings();
function submit() {
  errors.value = {};
  save.send(body(), secret(), {
    // 只換密碼時 GET 回來的 metadata 不變，watch(m) 不會觸發，所以這裡明確清空
    onSuccess: () => {
      password.value = '';
      clearPassword.value = false;
      $q.notify({ type: 'positive', message: t('ops.saved') });
    },
    onError: (e) => {
      const x = e as unknown as ApiError;
      errors.value = x.errors ?? {};
      if (!x.errors) $q.notify({ type: 'negative', message: x.message || t('ops.saveFailed') });
    },
  });
}
const test = useTestMailSettings();
function sendTest() {
  test.mutate(undefined, {
    onSuccess: (r) => $q.notify({ type: 'positive', message: t('ops.mail.testSent', { to: joinList(r.sentTo) }) }),
    onError: (e) => $q.notify({ type: 'negative', message: mailTestError(e as unknown as ApiError, t, te) }),
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="settings.isError.value" class="text-negative">{{ t('ops.loadFailed') }}</div>
    <div v-else-if="!m" class="text-muted">{{ t('designer.loading') }}</div>
    <div v-else class="column no-wrap q-gutter-y-md mail-page">
      <div class="text-h6">{{ t('ops.mail.title') }}</div>
      <div class="text-caption text-muted">{{ t('ops.mail.hint') }}</div>
      <q-banner v-if="readonly" dense class="bg-info-soft" data-testid="mail-readonly">{{ t('ops.mail.demoReadonly') }}</q-banner>
      <q-banner v-if="m.passwordUnreadable" dense class="bg-negative-soft" data-testid="mail-password-unreadable">{{ t('ops.mail.passwordUnreadable') }}</q-banner>
      <div class="row q-col-gutter-sm">
        <div class="col-12 col-sm-8">
          <q-input
            v-model="draft.host" dense outlined :label="t('ops.mail.host')" :readonly="readonly" :error="!!err('host')" :error-message="err('host')"
            data-testid="mail-host"
          />
        </div>
        <div class="col-12 col-sm-4">
          <q-input
            v-model="draft.port" type="number" dense outlined :label="t('ops.mail.port')" :readonly="readonly" :error="!!err('port')"
            :error-message="err('port')" data-testid="mail-port"
          />
        </div>
        <div class="col-12 col-sm-6">
          <q-select
            v-model="draft.security" :options="securityOptions" emit-value map-options dense outlined :label="t('ops.mail.securityLabel')"
            :readonly="readonly" :error="!!err('security')" :error-message="err('security')" data-testid="mail-security"
          />
        </div>
        <div class="col-12 col-sm-6">
          <q-input
            v-model="draft.userName" dense outlined :label="t('ops.mail.userName')" autocomplete="off" :readonly="readonly"
            :error="!!err('userName')" :error-message="err('userName')" data-testid="mail-user"
          />
        </div>
        <div class="col-12">
          <div class="row no-wrap items-start q-gutter-sm">
            <q-input
              v-model="password" class="col" type="password" autocomplete="new-password" dense outlined :label="t('ops.mail.password')" :hint="passwordHint"
              :readonly="readonly || clearPassword" :error="!!err('password')" :error-message="err('password')" data-testid="mail-password"
            />
            <q-btn
              v-if="m.hasPassword" flat dense no-caps :color="clearPassword ? 'primary' : 'negative'"
              :label="clearPassword ? t('ops.mail.keepPassword') : t('ops.mail.clearPassword')" :disable="readonly" data-testid="mail-clear-password"
              @click="clearPassword = !clearPassword; password = ''"
            />
          </div>
        </div>
        <div class="col-12 col-sm-6">
          <q-input
            v-model="draft.fromAddress" type="email" dense outlined :label="t('ops.mail.fromAddress')" :readonly="readonly"
            :error="!!err('fromAddress')" :error-message="err('fromAddress')" data-testid="mail-from"
          />
        </div>
        <div class="col-12 col-sm-6">
          <q-input
            v-model="draft.fromName" dense outlined :label="t('ops.mail.fromName')" :readonly="readonly" :error="!!err('fromName')"
            :error-message="err('fromName')" data-testid="mail-from-name"
          />
        </div>
      </div>
      <div class="row q-gutter-sm">
        <q-btn color="primary" no-caps :label="t('ops.save')" :disable="readonly" :loading="save.isPending.value" data-testid="mail-save" @click="submit" />
        <q-btn
          outline color="primary" no-caps :label="t('ops.mail.sendTest')" :disable="readonly || !m.configured" :loading="test.isPending.value"
          data-testid="mail-test" @click="sendTest"
        />
      </div>
      <div class="text-caption text-muted">{{ t('ops.mail.testHint') }}</div>
    </div>
  </q-page>
</template>

<style scoped>
.mail-page { max-width: 720px; }
</style>
