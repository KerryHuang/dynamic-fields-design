<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useSaveSmtpChannel, useSmtpChannel, useTestSmtpChannel } from 'src/application/dynamic/queries/ops-queries';
import { formatTime, mailTestError } from 'src/domain/ops/ops-format';
import { LOCALES } from 'src/i18n/locale';
import { joinList } from 'src/i18n/translate';
import type { ApiError } from 'src/services/api/http';

// 告警通道分頁（維運設計 §17.3 第 4 項；使用者裁決 2026-11-19 帳號信與告警信共用 SMTP 連線）：
// 只有通道自己的欄位（啟用、收件信箱、信件語系）＋寄測試信＋最近狀態；連線在「寄信設定」頁。Demo 站整頁唯讀。
const { t, te } = useI18n();
const $q = useQuasar();
const channel = useSmtpChannel();
const c = computed(() => channel.data.value);
const readonly = computed(() => !c.value?.editable);

const draft = ref({ enabled: false, toAddresses: [] as string[], mailLocale: null as string | null });
watch(c, (v) => {
  if (!v) return;
  draft.value = { enabled: v.enabled, toAddresses: [...v.toAddresses], mailLocale: v.mailLocale.isFactory ? null : v.mailLocale.value };
}, { immediate: true });

const localeName = (code: string) => LOCALES.find((l) => l.code === code)?.label ?? code;
const localeOptions = computed(() => [
  { value: null, label: t('ops.channel.localeFactory', { name: localeName(c.value?.mailLocale.factory ?? '') }) },
  ...LOCALES.map((l) => ({ value: l.code, label: l.label })),
]);

const errors = ref<Record<string, string[]>>({});
const err = (k: string) => (errors.value[k] ?? []).join('；');
const save = useSaveSmtpChannel();
function submit() {
  errors.value = {};
  save.mutate({ enabled: draft.value.enabled, toAddresses: draft.value.toAddresses, mailLocale: draft.value.mailLocale }, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('ops.saved') }),
    onError: (e) => {
      const x = e as unknown as ApiError;
      errors.value = x.errors ?? {};
      if (!x.errors) $q.notify({ type: 'negative', message: x.message || t('ops.saveFailed') });
    },
  });
}

const test = useTestSmtpChannel();
function sendTest() {
  test.mutate(undefined, {
    onSuccess: (r) => $q.notify({ type: 'positive', message: t('ops.mail.testSent', { to: joinList(r.sentTo) }) }),
    onError: (e) => $q.notify({ type: 'negative', message: mailTestError(e as unknown as ApiError, t, te) }),
  });
}

</script>

<template>
  <div v-if="channel.isError.value" class="text-negative">{{ t('ops.loadFailed') }}</div>
  <div v-else-if="!c" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else class="column no-wrap q-gutter-y-md channel-tab">
    <q-banner v-if="readonly" dense class="bg-info-soft" data-testid="channel-readonly">{{ t('ops.channel.demoReadonly') }}</q-banner>
    <q-banner v-if="!c.mailConfigured" dense class="bg-info-soft" data-testid="channel-mail-missing">
      {{ t('ops.channel.mailMissing') }}
      <template #action>
        <q-btn flat dense no-caps color="primary" to="/system/mail" :label="t('ops.channel.toMail')" data-testid="channel-to-mail" />
      </template>
    </q-banner>
    <div v-else-if="!c.enabled" class="text-muted" data-testid="channel-off">{{ t('ops.channel.off') }}</div>

    <q-toggle v-model="draft.enabled" :label="t('ops.channel.enabled')" :disable="readonly" data-testid="channel-enabled" />
    <q-select
      v-model="draft.toAddresses" multiple use-chips use-input new-value-mode="add-unique" hide-dropdown-icon dense outlined
      :label="t('ops.channel.toAddresses')" :hint="t('ops.channel.toAddressesHint')" :readonly="readonly" :error="!!err('toAddresses')"
      :error-message="err('toAddresses')" data-testid="channel-to"
    />
    <q-select
      v-model="draft.mailLocale" :options="localeOptions" emit-value map-options dense outlined :label="t('ops.channel.mailLocale')"
      :readonly="readonly" :error="!!err('mailLocale')" :error-message="err('mailLocale')" data-testid="channel-locale"
    />
    <div class="row q-gutter-sm">
      <q-btn color="primary" no-caps :label="t('ops.save')" :disable="readonly" :loading="save.isPending.value" data-testid="channel-save" @click="submit" />
      <q-btn
        outline color="primary" no-caps :label="t('ops.mail.sendTest')" :disable="readonly || !c.mailConfigured" :loading="test.isPending.value"
        data-testid="channel-test" @click="sendTest"
      />
    </div>
    <div class="text-caption text-muted">{{ t('ops.channel.testHint') }}</div>

    <q-list dense bordered separator data-testid="channel-status">
      <q-item>
        <q-item-section>
          <q-item-label caption>{{ t('ops.channel.lastSuccess') }}</q-item-label>
          <q-item-label>{{ formatTime(c.lastSuccessUtc) }}</q-item-label>
        </q-item-section>
      </q-item>
      <q-item>
        <q-item-section>
          <q-item-label caption>{{ t('ops.channel.lastFailure') }}</q-item-label>
          <q-item-label style="overflow-wrap: anywhere">
            {{ formatTime(c.lastFailureUtc) }}<template v-if="c.lastError">（{{ c.lastError }}）</template>
          </q-item-label>
        </q-item-section>
      </q-item>
      <q-item>
        <q-item-section>
          <q-item-label caption>{{ t('ops.channel.consecutiveFailures') }}</q-item-label>
          <q-item-label>{{ c.consecutiveFailures }}</q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
  </div>
</template>

<style scoped>
.channel-tab { max-width: 720px; }
</style>
