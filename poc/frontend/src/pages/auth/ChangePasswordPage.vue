<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useChangePassword, useLogout } from 'src/application/dynamic/queries/auth-queries';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t } = useI18n();
const router = useRouter();
const identity = useIdentityStore();
const change = useChangePassword();
const logoutMutation = useLogout();

const MIN_LENGTH = 8;
const oldPassword = ref('');
const newPassword = ref('');
const confirm = ref('');
const error = ref('');

const submit = () => {
  error.value = '';
  if (newPassword.value.length < MIN_LENGTH) { error.value = t('auth.passwordTooShort'); return; }
  if (newPassword.value !== confirm.value) { error.value = t('auth.passwordMismatch'); return; }
  change.mutate({ oldPassword: oldPassword.value, newPassword: newPassword.value }, {
    onSuccess: () => { void router.replace('/'); },
    onError: (e) => { error.value = leftoverMessages(e as unknown as ApiError, []).join('、') || t('auth.changeFailed'); },
  });
};

const logout = () => {
  error.value = '';
  logoutMutation.mutate(undefined, {
    onSuccess: () => { void router.replace('/login'); },
    onError: () => { error.value = t('auth.logoutFailed'); },
  });
};
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card style="width: 360px; max-width: 100%">
      <q-card-section class="text-h6">{{ t('auth.changePasswordTitle') }}</q-card-section>
      <q-form @submit.prevent="submit">
        <q-card-section class="q-gutter-sm">
          <div v-if="identity.mustChangePassword" class="text-caption" data-testid="cp-required">{{ t('auth.changeRequired') }}</div>
          <q-input v-model="oldPassword" :label="t('auth.currentPassword')" type="password" autocomplete="current-password" dense outlined data-testid="cp-old" />
          <q-input v-model="newPassword" :label="t('auth.newPassword')" type="password" autocomplete="new-password" dense outlined data-testid="cp-new" />
          <q-input v-model="confirm" :label="t('auth.confirmPassword')" type="password" autocomplete="new-password" dense outlined data-testid="cp-confirm" />
          <div v-if="error" class="text-negative" data-testid="cp-error">{{ error }}</div>
          <q-btn type="submit" color="primary" class="full-width" :label="t('auth.changePassword')" :loading="change.isPending.value" data-testid="cp-submit" />
        </q-card-section>
      </q-form>
      <q-card-actions>
        <q-btn v-if="!identity.mustChangePassword" flat no-caps :label="t('auth.backToApp')" data-testid="cp-back" @click="router.push('/')" />
        <q-space />
        <q-btn flat no-caps :label="t('auth.logout')" :loading="logoutMutation.isPending.value" data-testid="cp-logout" @click="logout" />
      </q-card-actions>
    </q-card>
  </div>
</template>
