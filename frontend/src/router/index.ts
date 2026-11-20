import { defineRouter } from '#q-app';
import { createRouter, createWebHistory } from 'vue-router';
import { i18n } from 'src/boot/i18n';
import { queryClient } from 'src/boot/tanstack-query';
import { setAuthHandlers } from 'src/services/api/http';
import { handlePasswordChangeRequired, handleUnauthorized, installAuthGuard } from './auth-guard';
import { routes } from './routes';

export default defineRouter(() => {
  const router = createRouter({ history: createWebHistory(), routes });
  installAuthGuard(router, i18n.global.locale);
  setAuthHandlers({
    onUnauthorized: () => handleUnauthorized(router, queryClient),
    onPasswordChangeRequired: () => handlePasswordChangeRequired(router),
  });
  return router;
});
