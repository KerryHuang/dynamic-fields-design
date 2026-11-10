<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useAuthOptions, useDemoLogin, useLogin } from 'src/application/dynamic/queries/auth-queries';
import type { AuthUser } from 'src/domain/dynamic/types';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import { safeRedirect } from 'src/router/auth-guard';
import type { ApiError } from 'src/services/api/http';

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const options = useAuthOptions();
const login = useLogin();
const demoLogin = useDemoLogin();

const userId = ref('');
const password = ref('');
const error = ref('');

const done = (u: AuthUser) => {
  const redirect = route.query.redirect;
  void router.replace(u.mustChangePassword ? '/change-password' : safeRedirect(typeof redirect === 'string' ? redirect : undefined));
};
// 只顯示已依語系產生的訊息：後端字串表的 401（登入失敗）、503（系統忙碌）與前端的網路錯誤；其餘（如 500 的英文標題）一律「登入失敗」
const fail = (e: unknown) => {
  const err = e as ApiError;
  const localized = err.status === 401 || err.status === 503 || err.code === 'network';
  error.value = (localized && err.message) || t('auth.loginFailed');
};

const submit = () => {
  error.value = '';
  login.mutate({ userId: userId.value, password: password.value }, { onSuccess: done, onError: fail });
};
const pickDemo = (id: string) => {
  error.value = '';
  demoLogin.mutate(id, { onSuccess: done, onError: fail });
};
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card style="width: 360px; max-width: 100%">
      <q-card-section class="row items-center no-wrap">
        <div class="text-h6 col">{{ t('auth.loginTitle') }}</div>
        <LocaleSwitcher />
      </q-card-section>
      <q-form @submit.prevent="submit">
        <q-card-section class="q-gutter-sm">
          <q-input v-model="userId" :label="t('auth.userId')" autocomplete="username" autofocus dense outlined data-testid="login-user" />
          <q-input v-model="password" :label="t('auth.password')" type="password" autocomplete="current-password" dense outlined data-testid="login-password" />
          <div v-if="error" class="text-negative" data-testid="login-error">{{ error }}</div>
          <q-btn type="submit" color="primary" class="full-width" :label="t('auth.login')" :loading="login.isPending.value" data-testid="login-submit" />
        </q-card-section>
      </q-form>
      <template v-if="options.data.value?.demoLoginEnabled">
        <q-separator />
        <q-card-section>
          <div class="text-subtitle2 q-mb-sm">{{ t('auth.demoTitle') }}</div>
          <div v-if="options.data.value.demoGenerating" class="text-caption" data-testid="demo-generating">{{ t('auth.demoGenerating') }}</div>
          <div class="q-gutter-xs" data-testid="demo-accounts">
            <q-btn
              v-for="a in options.data.value.demoAccounts"
              :key="a.userId"
              outline
              no-caps
              :label="a.displayName"
              :data-testid="`demo-account-${a.userId}`"
              @click="pickDemo(a.userId)"
            />
          </div>
        </q-card-section>
      </template>
    </q-card>
  </div>
</template>
