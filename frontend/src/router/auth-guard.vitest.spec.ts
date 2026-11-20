import { QueryClient } from '@tanstack/vue-query';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import type { AxiosResponse, InternalAxiosRequestConfig } from 'axios';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';
import { resetSessionSettings } from 'src/application/dynamic/session-settings';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import * as quasar from 'quasar';
import { BUILTIN_LOCALES, OFFLINE_LOCALES } from 'src/i18n/locale';
import { defaultLocale, enabledLocales, installedLocales, nativeName, setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';
import { resetStartupForTests, startLocale } from 'src/i18n/startup';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import enJson from 'shared/i18n/en-US/ui.json';
import { i18n as appI18n } from 'src/i18n/instance';
import { PREVIEW_KEY, setPreviewLocale } from 'src/i18n/locale-preview';
import { resetUiMessagesForTests, switchLocale } from 'src/i18n/ui-messages';
import { useLogout } from 'src/application/dynamic/queries/auth-queries';
import { VueQueryPlugin } from '@tanstack/vue-query';
import { mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';
import { LOCALE_KEY } from 'src/i18n/locale';
import { handleUnauthorized, installAuthGuard, safeRedirect } from './auth-guard';

const page = { template: '<i/>' };
const routes = [
  { path: '/', component: page },
  { path: '/login', component: page, meta: { public: true } },
  { path: '/change-password', component: page },
  { path: '/service-unavailable', component: page, meta: { public: true } },
  { path: '/dynamic/:moduleKey', component: page },
];

let meResponse: { status: number; data: unknown };
let meCalls: number;
let effResponse: { status: number; data: unknown };
let effCalls: number;
let optionsResponse: { status: number; data: unknown } | 'never';
let optionsCalls: number;
const effective = (zone: string) => ({ status: 200, data: { values: { 'locale.timeZone': zone } } });

function setup() {
  setActivePinia(createPinia());
  resetSessionSettings();
  resetStartupForTests();
  meCalls = 0;
  effCalls = 0;
  optionsCalls = 0;
  http.defaults.adapter = async (config) => {
    if (config.url === '/me') meCalls++;
    if (config.url === '/policies/effective') effCalls++;
    if (config.url === '/auth/options') optionsCalls++;
    if (config.url?.startsWith('/i18n/ui/')) return { data: {}, status: 200, statusText: 'OK', headers: { 'x-ui-hash': '' }, config };
    const r = config.url === '/policies/effective' ? effResponse : config.url === '/auth/options' ? optionsResponse : meResponse;
    if (r === 'never') return new Promise(() => {});
    if (r.status >= 400) return Promise.reject({ config, response: r });
    return { data: r.data, status: r.status, statusText: 'OK', headers: {}, config };
  };
  const router = createRouter({ history: createMemoryHistory(), routes });
  installAuthGuard(router);
  return router;
}

beforeEach(() => {
  effResponse = effective('+08:00');
  optionsResponse = { status: 200, data: { demoLoginEnabled: false, demoGenerating: false, demoAccounts: [], defaultLocale: 'zh-TW', enabledLocales: ['zh-TW', 'en-US', 'th', 'vi'], locales: BUILTIN_LOCALES } };
  localStorage.clear();
});

// 測試會改營運時區，結束後換回 vitest.setup 的出廠值
afterEach(() => {
  setBusinessOffset('+08:00');
  setLocaleInfos(BUILTIN_LOCALES);
  setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] });
});

describe('auth guard', () => {
  beforeEach(() => {
    meResponse = { status: 401, data: { message: '請先登入' } };
  });

  it('anonymous visitor is sent to /login with the target as redirect', async () => {
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/login');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order');
  });

  it('keeps the query string of the target in redirect', async () => {
    const router = setup();
    await router.push('/dynamic/order?q=a');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order?q=a');
  });

  it('feeds the theme from /me so a demo visitor cannot hit the save API before the layout loads', async () => {
    meResponse = { status: 200, data: { userId: 'demo', mustChangePassword: false, theme: { canSave: false, mode: null, style: null, defaultMode: 'system', defaultStyle: 'workbench', effectiveMode: 'system', effectiveStyle: 'workbench' } } };
    const router = setup();
    await router.push('/');
    expect(useThemeStore().canSave).toBe(false);
  });

  it('loads the session from /me once and lets a logged-in user through', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    const router = setup();
    await router.push('/dynamic/order');
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/');
    expect(useIdentityStore().userId).toBe('admin');
    expect(meCalls).toBe(1);
  });

  it('logged-in user visiting /login goes home', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    const router = setup();
    await router.push('/login');
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('mustChangePassword forces /change-password for every other page', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: true } };
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/change-password');
    await router.push('/change-password');
    expect(router.currentRoute.value.path).toBe('/change-password');
  });

  it('demo accounts cannot enter /change-password directly', async () => {
    meResponse = { status: 200, data: { userId: 'sales01', mustChangePassword: false, isDemoAccount: true } };
    const router = setup();
    await router.push('/change-password');
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('a demo account with a forced password change goes to /change-password without looping', async () => {
    meResponse = { status: 200, data: { userId: 'sales01', mustChangePassword: true, isDemoAccount: true } };
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/change-password');
    await router.push('/change-password');
    expect(router.currentRoute.value.path).toBe('/change-password');
  });

  it('a /me failure other than 401 shows service-unavailable, keeps the store, and probes again later', async () => {
    const router = setup();
    useIdentityStore().setSession('admin');
    meResponse = { status: 500, data: { title: 'boom' } };
    await router.push('/dynamic/order?q=1');
    expect(router.currentRoute.value.path).toBe('/service-unavailable');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order?q=1');
    expect(useIdentityStore().userId).toBe('admin');
    expect(meCalls).toBe(1);

    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    await router.push('/dynamic/order?q=1');
    expect(router.currentRoute.value.fullPath).toBe('/dynamic/order?q=1');
    expect(meCalls).toBe(2);
  });

  it('a network error also shows service-unavailable, and the page itself does not probe', async () => {
    const router = setup();
    meResponse = { status: 0, data: null };
    http.defaults.adapter = async () => Promise.reject(new Error('Network Error'));
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/service-unavailable');
    expect(meCalls).toBe(0);
  });

  it('anonymous visitor may open /login', async () => {
    const router = setup();
    await router.push('/login');
    expect(router.currentRoute.value.path).toBe('/login');
  });
});

describe('auth guard: business time zone (營運時區)', () => {
  const signedIn = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
  beforeEach(() => {
    meResponse = { status: 401, data: { message: '請先登入' } };
  });

  it('登入後設定營運時區', async () => {
    meResponse = signedIn;
    effResponse = effective('+07:00');
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/dynamic/order');
    expect(businessOffset()).toBe('+07:00');
    expect(effCalls).toBe(1);
  });

  it('取生效值失敗導到 service-unavailable', async () => {
    meResponse = signedIn;
    effResponse = { status: 500, data: { message: 'x' } };
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/service-unavailable');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order');
  });

  it('公開頁面導覽不需要營運時區', async () => {
    // 乾淨的模組：營運時區從沒設定過
    vi.resetModules();
    const freshHttp = (await import('src/services/api/http')).http;
    const freshTime = await import('src/domain/dynamic/business-time');
    const freshGuard = await import('./auth-guard');
    setActivePinia(createPinia());
    let calls = 0;
    freshHttp.defaults.adapter = async (config) => {
      if (config.url === '/policies/effective') calls++;
      return Promise.reject({ config, response: { status: 401, data: { message: '請先登入' } } });
    };
    const router = createRouter({ history: createMemoryHistory(), routes });
    freshGuard.installAuthGuard(router);
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/login');
    expect(calls).toBe(0);
    expect(freshTime.isBusinessOffsetLoaded()).toBe(false);
  });

  it('強制改密碼時政策 API 回 403 仍進得了改密碼頁，改完才載入', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: true } };
    effResponse = { status: 403, data: { code: 'passwordChangeRequired' } };
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/change-password');
    expect(effCalls).toBe(0);
    // 改完密碼（ChangePasswordPage 清旗標後導到首頁）
    useIdentityStore().mustChangePassword = false;
    effResponse = effective('+07:00');
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/');
    expect(effCalls).toBe(1);
    expect(businessOffset()).toBe('+07:00');
  });

  it('登入後的下一次導覽補載', async () => {
    effResponse = effective('+07:00');
    const router = setup();
    await router.push('/dynamic/order');
    expect(router.currentRoute.value.path).toBe('/login');
    expect(effCalls).toBe(0);
    useIdentityStore().setSession('admin', false);
    await router.push('/');
    expect(effCalls).toBe(1);
    expect(businessOffset()).toBe('+07:00');
    await router.push('/dynamic/order');
    expect(effCalls).toBe(1);
  });

  it('登出後重登入重新載入', async () => {
    meResponse = signedIn;
    effResponse = effective('+07:00');
    const router = setup();
    await router.push('/');
    expect(businessOffset()).toBe('+07:00');
    // useLogout 的 onSuccess：清身分、清「已載入」
    useIdentityStore().clear();
    resetSessionSettings();
    await router.push('/login');
    effResponse = effective('+08:00');
    useIdentityStore().setSession('admin', false);
    await router.push('/dynamic/order');
    expect(effCalls).toBe(2);
    expect(businessOffset()).toBe('+08:00');
  });

  it('session 過期後重登入重新載入', async () => {
    meResponse = signedIn;
    effResponse = effective('+07:00');
    const router = setup();
    await router.push('/dynamic/order');
    handleUnauthorized(router, new QueryClient());
    await new Promise((r) => setTimeout(r));
    expect(router.currentRoute.value.path).toBe('/login');
    effResponse = effective('+08:00');
    useIdentityStore().setSession('admin', false);
    await router.push('/dynamic/order');
    expect(effCalls).toBe(2);
    expect(businessOffset()).toBe('+08:00');
  });
});

describe('handleUnauthorized (session expiry mid-edit)', () => {
  beforeEach(() => {
    meResponse = { status: 401, data: { message: '請先登入' } };
  });

  const loggedIn = async (path: string): Promise<{ router: Router; qc: QueryClient }> => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    const router = setup();
    await router.push(path);
    const qc = new QueryClient();
    qc.setQueryData(['x'], 1);
    return { router, qc };
  };

  it('clears store and cache and goes to /login?redirect=<current path>', async () => {
    const { router, qc } = await loggedIn('/dynamic/order?tab=1');
    handleUnauthorized(router, qc);
    await new Promise((r) => setTimeout(r));
    expect(useIdentityStore().userId).toBe('');
    expect(qc.getQueryData(['x'])).toBeUndefined();
    expect(router.currentRoute.value.path).toBe('/login');
    expect(router.currentRoute.value.query.redirect).toBe('/dynamic/order?tab=1');
  });

  it('is a no-op when nobody is logged in (parallel 401s, or /me during the first navigation)', async () => {
    const router = setup();
    await router.push('/login');
    handleUnauthorized(router, new QueryClient());
    await new Promise((r) => setTimeout(r));
    expect(router.currentRoute.value.fullPath).toBe('/login');
  });
});

describe('safeRedirect', () => {
  it.each([
    ['//evil.example'], ['/\\evil.example'], ['https://evil.example'], ['evil'], [''], [undefined],
  ])('rejects %s', (p) => expect(safeRedirect(p)).toBe('/'));

  it.each([['/dynamic/order'], ['/dynamic/order?q=1'], ['/']])('accepts %s', (p) => expect(safeRedirect(p)).toBe(p));
});

describe('auth guard 語系政策', () => {
  // 同後端：locales 只含啟用語系
  const en = (enabled: string[], def = 'en-US') =>
    ({ status: 200, data: { defaultLocale: def, enabledLocales: enabled, locales: BUILTIN_LOCALES.filter((l) => enabled.includes(l.code)) } });
  const JA = { code: 'ja', nativeName: '日本語', quasarLang: null, fallbackCode: 'en-US' };
  beforeEach(() => {
    meResponse = { status: 401, data: { message: '請先登入' } };
  });

  // 「記的語系已停用就套預設」等決定語系的規則在 src/i18n/startup.ts（startup.vitest.spec.ts）；guard 只設定語系資訊與政策
  it('設定預設與啟用語系，不套語系', async () => {
    optionsResponse = en(['zh-TW', 'en-US']);
    const router = setup();
    await router.push('/login');
    expect(defaultLocale()).toBe('en-US');
    expect(enabledLocales()).toEqual(['zh-TW', 'en-US']);
  });

  it('Offline_ListsOfflineLocales：伺服器連不上時預設繁中、OFFLINE_LOCALES 可選（自稱取自內建清單）', async () => {
    setLocaleInfos([]);
    optionsResponse = { status: 500, data: null };
    const router = setup();
    await router.push('/login');
    expect(defaultLocale()).toBe('zh-TW');
    expect(enabledLocales()).toEqual(OFFLINE_LOCALES.map((l) => l.code));
    expect(enabledLocales()).toEqual(['zh-TW', 'en-US', 'th', 'vi']);
    expect(nativeName('th')).toBe('ไทย');
  });

  it('auth/options 的 locales 有新裝的語系時列進已安裝語系', async () => {
    optionsResponse = { status: 200, data: { defaultLocale: 'zh-TW', enabledLocales: ['zh-TW', 'ja'], locales: [BUILTIN_LOCALES[0], JA] } };
    const router = setup();
    await router.push('/login');
    expect(installedLocales()).toEqual(['zh-TW', 'ja']);
    expect(enabledLocales()).toEqual(['zh-TW', 'ja']);
  });

  it('ConsumesStartupOutcome：先 startLocale 再跑 guard，auth/options 共 1 次', async () => {
    optionsResponse = en(['zh-TW', 'en-US']);
    const router = setup();
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    await startLocale(i18n);
    await router.push('/login');
    expect(optionsCalls).toBe(1);
    expect(defaultLocale()).toBe('en-US');
  });

  it('OptionsNeverResolve_NavigationCompletes：auth/options 永不回，3000 ms 後導覽完成', async () => {
    vi.useFakeTimers();
    try {
      optionsResponse = 'never';
      const router = setup();
      let done = false;
      void router.push('/dynamic/order').then(() => { done = true; });
      await vi.advanceTimersByTimeAsync(2999);
      expect(done).toBe(false);
      await vi.advanceTimersByTimeAsync(1);
      await vi.waitFor(() => expect(done).toBe(true));
      expect(router.currentRoute.value.path).toBe('/login');
      expect(enabledLocales()).toEqual(OFFLINE_LOCALES.map((l) => l.code));
    } finally {
      vi.useRealTimers();
    }
  });

  it('登入後載入全系統生效值時更新三個語系設定', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: false } };
    effResponse = { status: 200, data: { values: { 'locale.timeZone': '+08:00', 'locale.default': 'en-US', 'locale.enabled': ['zh-TW', 'en-US'], 'locale.requiredText': ['en-US'] } } };
    const router = setup();
    await router.push('/');
    expect(defaultLocale()).toBe('en-US');
    expect(enabledLocales()).toEqual(['zh-TW', 'en-US']);
  });
});

describe('auth guard 試看（i4b 判斷 17）', () => {
  // ja 已安裝但未啟用：auth/options 的 locales 不含它，登入後生效值的 locales 才有
  const JA = { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US', uiHash: 'J' };
  const admin = (isSystemAdmin: boolean, userId = 'admin') => ({ status: 200, data: { userId, mustChangePassword: false, isSystemAdmin } });
  const withJa = { status: 200, data: { values: { 'locale.timeZone': '+08:00' }, locales: [...BUILTIN_LOCALES, JA] } };
  const flat = (o: unknown, p = ''): Record<string, string> => (typeof o === 'string' ? { [p]: o }
    : Object.entries(o as object).reduce((a, [k, v]) => ({ ...a, ...flat(v, p ? `${p}.${k}` : k) }), {}));
  let release: (() => void) | undefined;
  let slowJa = false;
  let uiCalls: string[];

  function previewSetup() {
    const router = setup();
    uiCalls = [];
    const base = http.defaults.adapter as (c: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
    http.defaults.adapter = async (config) => {
      if (config.url?.startsWith('/i18n/ui/')) {
        const code = config.url.slice('/i18n/ui/'.length);
        uiCalls.push(code);
        const ok = { data: { ...flat(enJson), 'menu.home': `home-${code}` }, status: 200, statusText: 'OK', headers: { 'x-ui-hash': code === 'ja' ? 'J' : code }, config };
        if (code === 'ja' && slowJa) return new Promise((r) => { release = () => r(ok); });
        if (code === 'th') return new Promise((r) => setTimeout(() => r(ok), 30));
        return ok;
      }
      return base(config);
    };
    return router;
  }

  beforeEach(() => {
    resetUiMessagesForTests();
    sessionStorage.clear();
    sessionStorage.setItem(PREVIEW_KEY, 'ja');
    appI18n.global.locale.value = 'zh-TW';
    slowJa = false;
    release = undefined;
    effResponse = withJa;
    meResponse = { status: 401, data: { message: '請先登入' } };
  });
  afterEach(() => {
    appI18n.global.locale.value = 'zh-TW';
    sessionStorage.clear();
  });

  it('匿名：清除', async () => {
    const router = previewSetup();
    await router.push('/login');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
  });

  it('強制改密碼：清除', async () => {
    meResponse = { status: 200, data: { userId: 'admin', mustChangePassword: true, isSystemAdmin: true } };
    const router = previewSetup();
    await router.push('/');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
  });

  it('不是系統管理員：清除，畫面是記住的語系', async () => {
    meResponse = admin(false);
    localStorage.setItem(LOCALE_KEY, 'zh-TW');
    const router = previewSetup();
    await router.push('/');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
    expect(appI18n.global.locale.value).toBe('zh-TW');
    expect(uiCalls).not.toContain('ja');
  });

  it('系統管理員、ja 已安裝：生效值載入後才判斷，背景載入並套用，不記住', async () => {
    meResponse = admin(true);
    const router = previewSetup();
    await router.push('/');
    expect(effCalls).toBe(1);
    await vi.waitFor(() => expect(appI18n.global.locale.value).toBe('ja'));
    expect(appI18n.global.t('menu.home')).toBe('home-ja');
    expect(localStorage.getItem(LOCALE_KEY)).toBeNull();
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBe('ja');
  });

  it('系統管理員但 ja 不在生效值的已安裝清單：清除', async () => {
    meResponse = admin(true);
    effResponse = { status: 200, data: { values: { 'locale.timeZone': '+08:00' }, locales: BUILTIN_LOCALES } };
    const router = previewSetup();
    await router.push('/');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
    expect(uiCalls).not.toContain('ja');
  });

  it('取生效值失敗：保留，下次成功時再驗', async () => {
    meResponse = admin(true);
    effResponse = { status: 500, data: { message: 'x' } };
    const router = previewSetup();
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/service-unavailable');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBe('ja');
  });

  it('handleUnauthorized：清除', async () => {
    meResponse = admin(true);
    const router = previewSetup();
    await router.push('/');
    await vi.waitFor(() => expect(appI18n.global.locale.value).toBe('ja'));
    handleUnauthorized(router, new QueryClient());
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
    await vi.waitFor(() => expect(appI18n.global.locale.value).toBe('zh-TW'));
  });

  describe('晚到的回應不套用', () => {
    beforeEach(() => { slowJa = true; meResponse = admin(true); });

    it('(a) 載入中登出（useLogout 成功）', async () => {
      const router = previewSetup();
      await router.push('/');
      await vi.waitFor(() => expect(release).toBeDefined());
      const base = http.defaults.adapter as (c: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
      http.defaults.adapter = async (config) => (config.url === '/auth/logout'
        ? { data: null, status: 204, statusText: 'OK', headers: {}, config } : base(config));
      let logout!: ReturnType<typeof useLogout>;
      mount(defineComponent({ setup() { logout = useLogout(); return () => h('i'); } }), { global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] } });
      await logout.mutateAsync();
      expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
      release!();
      await new Promise((r) => setTimeout(r, 10));
      expect(appI18n.global.locale.value).toBe('zh-TW');
    });

    it('(b) 載入中 session 失效（handleUnauthorized）：留在預設語系', async () => {
      const router = previewSetup();
      await router.push('/');
      await vi.waitFor(() => expect(release).toBeDefined());
      handleUnauthorized(router, new QueryClient());
      release!();
      await new Promise((r) => setTimeout(r, 10));
      expect(appI18n.global.locale.value).toBe('zh-TW');
    });

    it('(c) 載入中在選單選回 English', async () => {
      const router = previewSetup();
      await router.push('/');
      await vi.waitFor(() => expect(release).toBeDefined());
      // LocaleSwitcher 的做法：結束試看再切換
      setPreviewLocale(null);
      await switchLocale(appI18n, 'en-US');
      release!();
      await new Promise((r) => setTimeout(r, 10));
      expect(appI18n.global.locale.value).toBe('en-US');
    });
  });

  it('(d) 一般切換：選 th（慢）後立刻選 vi（快），最後是 vi', async () => {
    sessionStorage.clear();
    previewSetup();
    setLocaleInfos(BUILTIN_LOCALES.map((l) => ({ ...l, uiHash: l.code })));
    const slow = switchLocale(appI18n, 'th');
    const fast = switchLocale(appI18n, 'vi');
    await Promise.all([slow, fast]);
    expect(appI18n.global.locale.value).toBe('vi');
  });
});
