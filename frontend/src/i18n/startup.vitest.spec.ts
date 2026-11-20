import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import thJson from 'shared/i18n/th/ui.json';
import enJson from 'shared/i18n/en-US/ui.json';
import viJson from 'shared/i18n/vi/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { AuthOptions } from 'src/domain/dynamic/types';
import { resetSessionSettings } from 'src/application/dynamic/session-settings';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { BUILTIN_LOCALES, LOCALE_KEY } from './locale';
import { setLocaleInfos, setLocaleSettings } from './locale-settings';
import { PREVIEW_KEY, previewLocale, setPreviewLocale } from './locale-preview';
import { resetStartupForTests, startLocale, startupOutcome, takeStartupAuthOptions } from './startup';
import { UI_CACHE_KEY, cacheCurrent, installUiMessages, readUiCache, resetUiMessagesForTests, writeUiCache } from './ui-messages';

function leaves(o: unknown, path = ''): Record<string, string> {
  if (typeof o === 'string') return { [path]: o };
  return Object.entries(o as object).reduce((acc, [k, v]) => ({ ...acc, ...leaves(v, path ? `${path}.${k}` : k) }), {});
}
const TEXT: Record<string, Record<string, string>> = { 'zh-TW': leaves(zhTW), 'en-US': leaves(enJson), th: leaves(thJson), vi: leaves(viJson) };
const HOME: Record<string, string> = Object.fromEntries(Object.entries(TEXT).map(([c, m]) => [c, m['menu.home']!]));

const newI18n = () => createI18n({ legacy: false, locale: 'zh-TW', fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } as Record<string, typeof zhTW> });
const options = (def: string, enabled: string[], hashes: Record<string, string> = {}): AuthOptions => ({
  demoLoginEnabled: false, demoGenerating: false, demoAccounts: [], defaultLocale: def, enabledLocales: enabled,
  locales: BUILTIN_LOCALES.filter((l) => enabled.includes(l.code)).map((l) => ({ ...l, uiHash: hashes[l.code] ?? `H-${l.code}` })),
});
const never = <T>() => new Promise<T>(() => {});
const getOptions = () => vi.spyOn(dynamicApi, 'getAuthOptions');
const getUi = () => vi.spyOn(dynamicApi, 'getUiMessages');
/** API 回 code 的內建文字；hash 預設 H-<code>（與 options() 相同）。 */
const serve = (hash = (c: string) => `H-${c}`, text = (c: string) => TEXT[c]!) =>
  getUi().mockImplementation(async (code) => ({ hash: hash(code), messages: text(code) }));

beforeEach(() => {
  vi.useFakeTimers();
  localStorage.clear();
  sessionStorage.clear();
  resetStartupForTests();
  resetUiMessagesForTests();
  resetSessionSettings();
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  setLocaleInfos(BUILTIN_LOCALES);
  setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] });
});

describe('startLocale：有瀏覽器快取', () => {
  it('Cache_MountsWithoutWaiting：不等網路就結束，顯示快取的泰文', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    writeUiCache('th', 'A', TEXT.th!);
    getOptions().mockReturnValue(never());
    const i18n = newI18n();
    await startLocale(i18n);
    expect(i18n.global.locale.value).toBe('th');
    expect(i18n.global.t('menu.home')).toBe(HOME.th);
  });

  it('Cache_RevalidatesInBackground：雜湊不同才在背景重取並換上', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    writeUiCache('th', 'A', TEXT.th!);
    getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'th'], { th: 'B' }));
    const get = serve(() => 'B', () => ({ ...TEXT.th!, 'menu.home': 'ใหม่' }));
    const i18n = newI18n();
    await startLocale(i18n);
    await vi.advanceTimersByTimeAsync(0);
    expect(get).toHaveBeenCalledWith('th', 'B');
    expect(i18n.global.t('menu.home')).toBe('ใหม่');
    expect(readUiCache()?.hash).toBe('B');
  });

  it('雜湊相同就不取文字', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    writeUiCache('th', 'A', TEXT.th!);
    getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'th'], { th: 'A' }));
    const get = serve();
    await startLocale(newI18n());
    await vi.advanceTimersByTimeAsync(0);
    expect(get).not.toHaveBeenCalled();
  });

  it('NoStoredLocale_CacheUsed：沒選過、快取是預設語系就直接用；預設改了在背景換過去', async () => {
    writeUiCache('th', 'H-th', TEXT.th!);
    getOptions().mockResolvedValue(options('vi', ['zh-TW', 'th', 'vi']));
    serve();
    const i18n = newI18n();
    await startLocale(i18n);
    expect(i18n.global.locale.value).toBe('th');
    await vi.advanceTimersByTimeAsync(0);
    expect(i18n.global.locale.value).toBe('vi');
    expect(i18n.global.t('menu.home')).toBe(HOME.vi);
    expect(readUiCache()?.code).toBe('vi');
    expect(localStorage.getItem(LOCALE_KEY)).toBeNull();
  });

  it('沒選過、預設仍是快取的語系就留著', async () => {
    writeUiCache('th', 'H-th', TEXT.th!);
    getOptions().mockResolvedValue(options('th', ['zh-TW', 'th']));
    serve();
    const i18n = newI18n();
    await startLocale(i18n);
    await vi.advanceTimersByTimeAsync(0);
    expect(i18n.global.locale.value).toBe('th');
  });

  it('記的語系已停用：背景改用預設語系', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    writeUiCache('th', 'H-th', TEXT.th!);
    getOptions().mockResolvedValue(options('en-US', ['zh-TW', 'en-US']));
    serve();
    const i18n = newI18n();
    await startLocale(i18n);
    await vi.advanceTimersByTimeAsync(0);
    expect(i18n.global.locale.value).toBe('en-US');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('th');
  });

  it('OptionsFail_UsesCache：auth/options 失敗仍用快取', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    writeUiCache('th', 'A', TEXT.th!);
    getOptions().mockRejectedValue(new Error('down'));
    const i18n = newI18n();
    await startLocale(i18n);
    await vi.advanceTimersByTimeAsync(3000);
    expect(i18n.global.t('menu.home')).toBe(HOME.th);
  });

  it('記住的 ja 先載同名的 Quasar 包，拿到語系資訊（quasarLang: null）後改套 en-US', async () => {
    vi.useRealTimers();
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    localStorage.setItem(LOCALE_KEY, 'ja');
    writeUiCache('ja', 'J', TEXT['en-US']!);
    let release!: (o: AuthOptions) => void;
    const o = options('zh-TW', ['zh-TW', 'en-US']);
    o.enabledLocales.push('ja');
    o.locales.push({ code: 'ja', nativeName: '日本語', quasarLang: null, fallbackCode: 'en-US', uiHash: 'J' });
    getOptions().mockReturnValue(new Promise((r) => { release = r; }));
    const i18n = newI18n();
    await startLocale(i18n);
    await vi.waitFor(() => expect(setLang.mock.calls.at(-1)?.[0].isoName).toBe('ja'));
    release(o);
    await vi.waitFor(() => expect(setLang.mock.calls.at(-1)?.[0].isoName).toBe('en-US'));
    expect(i18n.global.locale.value).toBe('ja');
  });
});

describe('startLocale：沒有快取', () => {
  it('NoCache_WaitsForBoth：依序取 auth/options 與文字，resolve 時已是英文', async () => {
    localStorage.setItem(LOCALE_KEY, 'en-US');
    const opt = getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'en-US']));
    const get = serve();
    const i18n = newI18n();
    await startLocale(i18n);
    expect(get).toHaveBeenCalledWith('en-US', 'H-en-US');
    expect(i18n.global.locale.value).toBe('en-US');
    expect(i18n.global.t('menu.home')).toBe(HOME['en-US']);
    await startupOutcome();
    expect(opt).toHaveBeenCalledTimes(1);
    expect(readUiCache()?.code).toBe('en-US');
  });

  it('NoStoredLocale_UsesDefault：沒記語系就用預設語系並寫快取', async () => {
    getOptions().mockResolvedValue(options('th', ['zh-TW', 'th']));
    serve();
    const i18n = newI18n();
    await startLocale(i18n);
    expect(i18n.global.locale.value).toBe('th');
    expect(readUiCache()).toMatchObject({ code: 'th', hash: 'H-th' });
    expect(localStorage.getItem(LOCALE_KEY)).toBeNull();
  });

  it('StoredLocaleDisabled_UsesDefault：記的語系已停用就套預設', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    getOptions().mockResolvedValue(options('en-US', ['zh-TW', 'en-US']));
    serve();
    const i18n = newI18n();
    await startLocale(i18n);
    expect(i18n.global.locale.value).toBe('en-US');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('th');
  });

  it('OptionsNeverResolve_BudgetEnds：3000 ms 時兩者都 resolve、畫面繁中', async () => {
    getOptions().mockReturnValue(never());
    const i18n = newI18n();
    let done = false;
    void startLocale(i18n).then(() => { done = true; });
    let outcome: unknown;
    void startupOutcome().then((o) => { outcome = o; });
    await vi.advanceTimersByTimeAsync(2999);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
    expect(outcome).toEqual({ options: null });
    expect(i18n.global.t('menu.home')).toBe(HOME['zh-TW']);
  });

  it('TextNeverResolve_BudgetEnds：文字不回，3000 ms 時 resolve、畫面繁中，startupOutcome 帶 options', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'th']));
    getUi().mockReturnValue(never());
    const i18n = newI18n();
    let done = false;
    void startLocale(i18n).then(() => { done = true; });
    await vi.advanceTimersByTimeAsync(2999);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
    expect(i18n.global.t('menu.home')).toBe(HOME['zh-TW']);
    expect((await startupOutcome()).options?.defaultLocale).toBe('zh-TW');
  });

  it('Timeout_MountsZhTwThenSwaps：文字 3 秒後才回，先顯示繁中、回來後換泰文並寫快取', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'th']));
    getUi().mockImplementation(() => new Promise((r) => setTimeout(() => r({ hash: 'H-th', messages: TEXT.th! }), 4000)));
    const i18n = newI18n();
    const p = startLocale(i18n);
    await vi.advanceTimersByTimeAsync(3000);
    await p;
    expect(i18n.global.locale.value).toBe('th');
    expect(i18n.global.t('menu.home')).toBe(HOME['zh-TW']);
    await vi.advanceTimersByTimeAsync(1000);
    expect(i18n.global.t('menu.home')).toBe(HOME.th);
    expect(readUiCache()?.code).toBe('th');
  });

  it('OptionsFail_NoCache_ZhTw：auth/options 失敗又沒有快取就繁中', async () => {
    getOptions().mockRejectedValue(new Error('down'));
    const i18n = newI18n();
    await startLocale(i18n);
    expect(i18n.global.locale.value).toBe('zh-TW');
    expect(i18n.global.t('menu.home')).toBe(HOME['zh-TW']);
  });

  it('ZhTw_FetchedForOverrides：繁中也取一次，期間顯示打包的繁中', async () => {
    localStorage.setItem(LOCALE_KEY, 'zh-TW');
    getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'en-US']));
    let release!: () => void;
    const get = getUi().mockImplementation(() => new Promise((r) => { release = () => r({ hash: 'H-zh-TW', messages: { ...TEXT['zh-TW']!, 'menu.home': '主頁' } }); }));
    const i18n = newI18n();
    await startLocale(i18n);
    expect(get).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledWith('zh-TW', 'H-zh-TW');
    expect(i18n.global.t('menu.home')).toBe(HOME['zh-TW']);
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(i18n.global.t('menu.home')).toBe('主頁');
  });

  it('auth/options 預算後才回來：背景補上語系資訊與文字', async () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    getOptions().mockImplementation(() => new Promise((r) => setTimeout(() => r(options('zh-TW', ['zh-TW', 'th'])), 5000)));
    serve();
    const i18n = newI18n();
    const p = startLocale(i18n);
    await vi.advanceTimersByTimeAsync(3000);
    await p;
    expect(i18n.global.locale.value).toBe('th');
    expect(i18n.global.t('menu.home')).toBe(HOME['zh-TW']);
    await vi.advanceTimersByTimeAsync(2000);
    expect(i18n.global.t('menu.home')).toBe(HOME.th);
  });
});

describe('startLocale：試看', () => {
  it('Preview_IgnoredAtStartup：啟動不套試看值、不取也不寫快取', async () => {
    sessionStorage.setItem(PREVIEW_KEY, 'ja');
    localStorage.setItem(LOCALE_KEY, 'th');
    getOptions().mockResolvedValue(options('zh-TW', ['zh-TW', 'th']));
    const get = serve();
    const i18n = newI18n();
    await startLocale(i18n);
    expect(i18n.global.locale.value).toBe('th');
    expect(get.mock.calls.map((c) => c[0])).not.toContain('ja');
    expect(readUiCache()?.code).toBe('th');
  });
});

describe('takeStartupAuthOptions', () => {
  it('啟動那次成功才給、只給一次', async () => {
    const o = options('zh-TW', ['zh-TW']);
    getOptions().mockResolvedValue(o);
    serve();
    await startLocale(newI18n());
    expect(takeStartupAuthOptions()).toBe(o);
    expect(takeStartupAuthOptions()).toBeUndefined();
  });
  it('啟動那次失敗就不給', async () => {
    getOptions().mockRejectedValue(new Error('x'));
    await startLocale(newI18n());
    expect(takeStartupAuthOptions()).toBeUndefined();
  });
});

describe('locale-preview', () => {
  it('寫入 sessionStorage，不碰 formwright.locale；null 移除', () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    setPreviewLocale('ja');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBe('ja');
    expect(previewLocale()).toBe('ja');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('th');
    setPreviewLocale(null);
    expect(previewLocale()).toBeNull();
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
  });
  it('sessionStorage 不可用時不丟例外、讀回 null', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(() => setPreviewLocale('ja')).not.toThrow();
    expect(previewLocale()).toBeNull();
  });
  it('試看中的語系不寫瀏覽器快取', () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'ja', TEXT['en-US']!, 'J');
    i18n.global.locale.value = 'ja';
    setPreviewLocale('ja');
    cacheCurrent(i18n);
    expect(localStorage.getItem(UI_CACHE_KEY)).toBeNull();
    setPreviewLocale(null);
    cacheCurrent(i18n);
    expect(readUiCache()?.code).toBe('ja');
  });
});

describe('打包檢查', () => {
  it('boot 與 i18n 單例只 import 繁中的介面文字', () => {
    const src = ['src/boot/i18n.ts', 'src/i18n/instance.ts'].map((f) => readFileSync(resolve(process.cwd(), f), 'utf8')).join('\n');
    const imports = [...src.matchAll(/shared\/i18n\/([^/]+)\/ui\.json/g)].map((m) => m[1]);
    expect(imports).toEqual(['zh-TW']);
  });
});
