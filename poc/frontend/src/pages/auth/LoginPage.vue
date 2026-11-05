<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useAuthOptions, useDemoLogin, useLogin } from 'src/application/dynamic/queries/auth-queries';
import type { AuthUser } from 'src/domain/dynamic/types';
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
const fail = (e: unknown) => { error.value = (e as ApiError).message ?? t('auth.loginFailed'); };

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
      <q-card-section class="text-h6">{{ t('auth.loginTitle') }}</q-card-section>
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
          <div v-else class="q-gutter-xs" data-testid="demo-accounts">
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
