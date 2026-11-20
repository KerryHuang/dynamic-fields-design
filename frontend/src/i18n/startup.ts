import { sessionSettingsLoaded } from 'src/application/dynamic/session-settings';
import type { AuthOptions } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { FALLBACK_LOCALE, OFFLINE_LOCALES, STARTUP_BUDGET_MS, applyDocumentLocale, applyLocale, readLocale, type LocaleCode } from './locale';
import { defaultLocale, enabledLocales, localeInfo, setLocaleInfos, setLocaleSettings, toLocaleCodes, type LocaleInfo } from './locale-settings';
import { cacheCurrent, ensureCurrentMessages, installUiMessages, loadedHash, readUiCache, refreshLoadedMessages, type I18nLike } from './ui-messages';

// 啟動順序（語系包設計 §8.2、i4b 計畫判斷 12、13）：首屏只打一次 auth/options，boot、auth guard、登入頁第一次掛載共用；
// 決定語系與載入介面文字都在這裡，guard 只消費結果設定語系資訊與政策。

export interface StartupOutcome { options: AuthOptions | null }

let started: { fetch: Promise<AuthOptions>; outcome: Promise<StartupOutcome> } | undefined;
let untaken: AuthOptions | undefined;
const noop = () => {};

function start() {
  if (started) return started;
  const fetch = dynamicApi.getAuthOptions();
  fetch.then((o) => { untaken = o; }, noop);
  const outcome = new Promise<StartupOutcome>((resolve) => {
    const timer = setTimeout(() => resolve({ options: null }), STARTUP_BUDGET_MS);
    fetch.then((o) => resolve({ options: o }), () => resolve({ options: null })).finally(() => clearTimeout(timer));
  });
  started = { fetch, outcome };
  return started;
}

/** 啟動那一次 auth/options 的結果：最晚在 STARTUP_BUDGET_MS 時 resolve（逾時或失敗是 options: null），永不 reject。 */
export const startupOutcome = (): Promise<StartupOutcome> => start().outcome;

/** 啟動那次成功且還沒被取用時給一次（登入頁第一次掛載用）；之後的重取照常發新請求。 */
export function takeStartupAuthOptions(): AuthOptions | undefined {
  const o = untaken;
  untaken = undefined;
  return o;
}

export function resetStartupForTests(): void {
  started = undefined;
  untaken = undefined;
}

/**
 * 依 auth/options 設定語系資訊與預設、啟用語系；取不到時（伺服器連不上）預設繁中、OFFLINE_LOCALES 可選（i4b 待決 Q1）。
 * 之後登入後的全系統生效值會再更新。
 */
export function applyStartupOptions(o: AuthOptions | null): void {
  let infos: LocaleInfo[] = OFFLINE_LOCALES;
  let def = FALLBACK_LOCALE;
  let enabled = OFFLINE_LOCALES.map((l) => l.code);
  // 形狀不對（舊版後端、測試替身）當成取不到
  if (o && Array.isArray(o.locales)) {
    setLocaleInfos(o.locales);
    const list = toLocaleCodes(o.enabledLocales);
    const d = toLocaleCodes([o.defaultLocale])[0];
    if (d && list.length) { infos = o.locales; def = d; enabled = list; }
  }
  setLocaleInfos(infos);
  setLocaleSettings({ defaultLocale: def, enabled });
}

/**
 * 背景：拿到 auth/options 後（登入後的生效值已載入就不覆蓋語系設定），目前語系已停用、或沒選過而預設語系改了 → 載入預設語系後切過去；
 * 否則重套 Quasar 語言包（啟動時還沒有語系資訊），雜湊不同才重取文字。使用者已經自己切了語言就不動。
 */
function settle(i18n: I18nLike, o: AuthOptions, code: LocaleCode, followDefault: boolean): void {
  if (!sessionSettingsLoaded()) applyStartupOptions(o);
  const locale = i18n.global.locale;
  if (locale.value !== code) return;
  const def = defaultLocale();
  const next = !enabledLocales().includes(code) || (followDefault && code !== def) ? def : code;
  if (next === code) {
    applyDocumentLocale(code);
    const info = localeInfo(code);
    if (!info) return;
    if (loadedHash(code) === undefined) void ensureCurrentMessages(i18n, code).catch(noop);
    else void refreshLoadedMessages(i18n, [info]);
    return;
  }
  void ensureCurrentMessages(i18n, next).then(() => {
    if (locale.value !== code) return;
    applyLocale(next, locale, false);
    cacheCurrent(i18n);
  }, noop);
}

/** 開機時決定語系並載入介面文字；有快取時不等網路，沒有時最多等 STARTUP_BUDGET_MS。試看值在這裡不用（判斷 17）。 */
export async function startLocale(i18n: I18nLike): Promise<void> {
  const locale = i18n.global.locale;
  const stored = readLocale();
  const cache = readUiCache();
  const s = start();
  let code = stored ?? cache?.code ?? null;

  if (code && cache?.code === code) {
    installUiMessages(i18n, code, cache.messages, cache.hash);
    applyLocale(code, locale, false);
    const c = code;
    s.fetch.then((o) => settle(i18n, o, c, !stored), noop);
    return;
  }

  const budget = new Promise<false>((resolve) => setTimeout(() => resolve(false), STARTUP_BUDGET_MS));
  const { options } = await s.outcome;
  applyStartupOptions(options);
  if (options) {
    code ??= defaultLocale();
    if (!enabledLocales().includes(code)) code = defaultLocale();
  } else {
    code ??= FALLBACK_LOCALE;
    if (!OFFLINE_LOCALES.some((l) => l.code === code)) code = FALLBACK_LOCALE;
  }
  const c = code;
  if (!options) {
    applyLocale(c, locale, false);
    // 預算用完才回來：照常交給背景
    s.fetch.then((o) => settle(i18n, o, c, !stored), noop);
    return;
  }
  const load = ensureCurrentMessages(i18n, c).then(() => true, () => false);
  // 繁中有打包版本可以先顯示，不等
  if (c !== FALLBACK_LOCALE) await Promise.race([load, budget]);
  applyLocale(c, locale, false);
  void load.then((ok) => { if (ok && locale.value === c) cacheCurrent(i18n); });
}
