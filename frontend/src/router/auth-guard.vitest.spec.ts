import { QueryClient } from '@tanstack/vue-query';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { useThemeStore } from 'src/stores/use-theme-store';
import { resetSessionSettings } from 'src/application/dynamic/session-settings';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import * as quasar from 'quasar';
import { BUILTIN_LOCALES, LOCALE_KEY, applyLocale } from 'src/i18n/locale';
import { defaultLocale, enabledLocales, installedLocales, nativeName, setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';
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
let optionsResponse: { status: number; data: unknown };
let uiLocale: { value: string };
const effective = (zone: string) => ({ status: 200, data: { values: { 'locale.timeZone': zone } } });

function setup() {
  setActivePinia(createPinia());
  resetSessionSettings();
  meCalls = 0;
  effCalls = 0;
  http.defaults.adapter = async (config) => {
    if (config.url === '/me') meCalls++;
    if (config.url === '/policies/effective') effCalls++;
    const r = config.url === '/policies/effective' ? effResponse : config.url === '/auth/options' ? optionsResponse : meResponse;
    if (r.status >= 400) return Promise.reject({ config, response: r });
    return { data: r.data, status: r.status, statusText: 'OK', headers: {}, config };
  };
  const router = createRouter({ history: createMemoryHistory(), routes });
  uiLocale = { value: 'zh-TW' };
  installAuthGuard(router, uiLocale);
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

  it('沒記過語系時套用預設語系，不寫入 localStorage', async () => {
    optionsResponse = en(['zh-TW', 'en-US']);
    const router = setup();
    await router.push('/login');
    expect(uiLocale.value).toBe('en-US');
    expect(localStorage.getItem(LOCALE_KEY)).toBeNull();
    expect(defaultLocale()).toBe('en-US');
    expect(enabledLocales()).toEqual(['zh-TW', 'en-US']);
  });

  it('記的語系仍啟用時沿用，不改', async () => {
    optionsResponse = en(['zh-TW', 'en-US']);
    localStorage.setItem(LOCALE_KEY, 'zh-TW');
    const router = setup();
    await router.push('/login');
    expect(uiLocale.value).toBe('zh-TW');
  });

  it('記的語系已停用時改用預設語系', async () => {
    optionsResponse = en(['zh-TW', 'en-US']);
    localStorage.setItem(LOCALE_KEY, 'th');
    const router = setup();
    await router.push('/login');
    expect(uiLocale.value).toBe('en-US');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('th');
  });

  it('伺服器連不上時維持繁中、內建四語可選（自稱取自內建清單）', async () => {
    setLocaleInfos([]);
    optionsResponse = { status: 500, data: null };
    const router = setup();
    await router.push('/login');
    expect(uiLocale.value).toBe('zh-TW');
    expect(defaultLocale()).toBe('zh-TW');
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

  it('記過 ja 但 ja 沒啟用時套用預設語系', async () => {
    optionsResponse = en(['zh-TW', 'en-US']);
    localStorage.setItem(LOCALE_KEY, 'ja');
    const router = setup();
    await router.push('/login');
    expect(uiLocale.value).toBe('en-US');
  });

  it('完整啟動：記住的 ja 先載同名的 Quasar 包，拿到語系資訊（quasarLang: null）後改套 en-US', async () => {
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    localStorage.setItem(LOCALE_KEY, 'ja');
    optionsResponse = { status: 200, data: { defaultLocale: 'zh-TW', enabledLocales: ['zh-TW', 'en-US', 'ja'], locales: [...BUILTIN_LOCALES.slice(0, 2), JA] } };
    const router = setup();
    applyLocale('ja', uiLocale, false); // boot/i18n.ts 的做法
    await vi.waitFor(() => expect(setLang.mock.calls.at(-1)?.[0].isoName).toBe('ja'));
    await router.push('/login');
    await vi.waitFor(() => expect(setLang.mock.calls.at(-1)?.[0].isoName).toBe('en-US'));
    expect(uiLocale.value).toBe('ja');
    setLang.mockRestore();
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
