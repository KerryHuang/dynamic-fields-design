import { defineBoot } from '#q-app';
import { Notify } from 'quasar';
import { currentLocale, tFor } from 'src/i18n/translate';
import { createReporter, isChunkLoadError, sendReport, type ClientErrorKind } from 'src/services/ops/error-reporter';

let updatePromptShown = false;

/** 部署新版後舊頁面載不到程式塊：提示重新整理，整個分頁只顯示一次。 */
function showUpdatePrompt() {
  if (updatePromptShown) return;
  updatePromptShown = true;
  const t = tFor(currentLocale());
  Notify.create({
    type: 'warning', message: t('ops.updated'), timeout: 0,
    actions: [{ label: t('ops.reload'), color: 'white', handler: () => location.reload() }],
  });
}

// 錯誤回報掛在 boot：Vue errorHandler、window error／unhandledrejection、router onError、Vite 預載失敗（設計 §10.3）
export default defineBoot(({ app, router }) => {
  const reporter = createReporter({ send: sendReport, now: () => Date.now() });
  const report = (kind: ClientErrorKind, error: unknown) => {
    const chunk = isChunkLoadError(error);
    if (chunk) showUpdatePrompt();
    reporter.report(chunk ? 'chunk' : kind, error, router.currentRoute.value);
  };

  app.config.errorHandler = (err) => {
    // 設了 errorHandler 之後 Vue 不再自己印，保留原本的 console 輸出
    console.error(err);
    report('vue', err);
  };
  window.addEventListener('error', (e) => report('window', e.error ?? e.message));
  window.addEventListener('unhandledrejection', (e) => report('promise', e.reason));
  router.onError((err) => report('vue', err));
  window.addEventListener('vite:preloadError', (e) => {
    showUpdatePrompt();
    reporter.report('chunk', (e as Event & { payload?: unknown }).payload, router.currentRoute.value);
  });
});
