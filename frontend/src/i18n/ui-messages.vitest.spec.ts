import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n, useI18n } from 'vue-i18n';
import thJson from 'shared/i18n/th/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { BUILTIN_LOCALES } from './locale';
import { setLocaleInfos, type LocaleInfo } from './locale-settings';
import {
  UI_CACHE_KEY, UI_FETCH_TIMEOUT_MS, ensureLocaleMessages, hasUiMessages, installUiMessages, loadedHash, prepareMessages,
  readUiCache, refreshLoadedMessages, resetUiMessagesForTests, writeUiCache,
} from './ui-messages';

function leaves(o: unknown, path = ''): Record<string, string> {
  if (typeof o === 'string') return { [path]: o };
  return Object.entries(o as object).reduce((acc, [k, v]) => ({ ...acc, ...leaves(v, path ? `${path}.${k}` : k) }), {});
}
const base = leaves(zhTW);
const th = leaves(thJson);
const ja = { ...leaves(thJson), 'menu.home': 'ホーム' };
const newI18n = () => createI18n({ legacy: false, locale: 'zh-TW', fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } as Record<string, typeof zhTW> });
const info = (code: string, uiHash: string): LocaleInfo => ({ code, nativeName: code, quasarLang: null, fallbackCode: null, uiHash });
const withHashes = (h: Record<string, string>) => setLocaleInfos(Object.entries(h).map(([c, v]) => info(c, v)));
const deferred = <T>() => {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
};
const api = () => vi.spyOn(dynamicApi, 'getUiMessages');

beforeEach(() => { resetUiMessagesForTests(); localStorage.clear(); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllEnvs(); vi.useRealTimers(); setLocaleInfos(BUILTIN_LOCALES); });

describe('prepareMessages', () => {
  it('合法句子原樣、轉成巢狀', () => {
    const r = prepareMessages({ 'menu.home': 'หน้าแรก', 'auth.login': 'เข้าสู่ระบบ' }, base);
    expect((r.nested as { menu: { home: string } }).menu.home).toBe('หน้าแรก');
    expect(r.dropped).toEqual([]);
  });
  it('編譯失敗的句子丟掉並改用繁中；基準外的鍵、非字串值丟掉；缺的鍵列在 missing', () => {
    const r = prepareMessages({ ...th, 'menu.home': '{name', 'auth.login': 7, 'no.such.key': 'x' }, base);
    const n = r.nested as { menu: { home: string }; auth: { login: string }; no?: unknown };
    expect(n.menu.home).toBe(base['menu.home']);
    expect(n.auth.login).toBe(base['auth.login']);
    expect(n.no).toBeUndefined();
    expect(r.dropped).toEqual(expect.arrayContaining(['menu.home', 'auth.login', 'no.such.key']));
    expect(r.missing).toEqual([]);
    const partial = prepareMessages({ 'menu.home': 'หน้าแรก' }, base);
    expect(partial.missing).toContain('auth.login');
  });
  it('裸 @ 編譯失敗會丟掉；字面量寫法合法', () => {
    const r = prepareMessages({ 'menu.home': 'a@', 'auth.login': "{'@'}{'{{欄位}}'}" }, base);
    expect(r.dropped).toEqual(['menu.home']);
    expect((r.nested as { auth: { login: string } }).auth.login).toBe("{'@'}{'{{欄位}}'}");
  });
  it('裝上後 t() 取得到', () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    expect(i18n.global.t('menu.home', {}, { locale: 'th' })).toBe('หน้าแรก');
    expect(hasUiMessages(i18n, 'th')).toBe(true);
    expect(loadedHash('th')).toBe('A');
  });
  it('開發模式缺鍵與壞句各印一次，正式模式不印', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    vi.stubEnv('DEV', true);
    installUiMessages(newI18n(), 'th', { 'menu.home': '{bad' }, 'A');
    expect(warn).toHaveBeenCalledTimes(2);
    warn.mockClear();
    vi.stubEnv('DEV', false);
    installUiMessages(newI18n(), 'th', { 'menu.home': '{bad' }, 'A');
    expect(warn).not.toHaveBeenCalled();
  });
  it('翻譯一律當純文字輸出（設計 §13.3）', () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', { ...th, 'menu.home': '<b>粗體</b>' }, 'A');
    i18n.global.locale.value = 'th';
    const C = defineComponent({ setup() { const { t } = useI18n(); return () => h('div', t('menu.home')); } });
    const w = mount(C, { global: { plugins: [i18n] } });
    expect(w.text()).toContain('<b>');
    expect(w.find('b').exists()).toBe(false);
  });
});

describe('ensureLocaleMessages', () => {
  it('單一語系失敗不丟例外、不在回傳清單；同時呼叫兩次只打一次', async () => {
    withHashes({ 'zh-TW': '', ja: 'J', ko: 'K' });
    const get = api().mockImplementation(async (code) => {
      if (code === 'ja') return { hash: 'J', messages: ja };
      throw new Error('down');
    });
    const i18n = newI18n();
    const [a, b] = await Promise.all([ensureLocaleMessages(i18n, ['zh-TW', 'ja', 'ko']), ensureLocaleMessages(i18n, ['ja'])]);
    expect(a).toEqual(['zh-TW', 'ja']);
    expect(b).toEqual(['ja']);
    expect(get.mock.calls.filter((c) => c[0] === 'ja')).toHaveLength(1);
    expect(get).toHaveBeenCalledWith('ja', 'J');
    await ensureLocaleMessages(i18n, ['ja']);
    expect(get.mock.calls.filter((c) => c[0] === 'ja')).toHaveLength(1);
  });
  it('zh-TW 有雜湊且和已裝的不同時也取一次（覆寫層，U1）', async () => {
    withHashes({ 'zh-TW': 'Z' });
    const get = api().mockResolvedValue({ hash: 'Z', messages: { ...base, 'menu.home': '主頁' } });
    const i18n = newI18n();
    expect(await ensureLocaleMessages(i18n, ['zh-TW'])).toEqual(['zh-TW']);
    expect(get).toHaveBeenCalledWith('zh-TW', 'Z');
    expect(i18n.global.t('menu.home')).toBe('主頁');
  });
  it('預算到時 resolve，沒回來的語系在背景繼續', async () => {
    vi.useFakeTimers();
    withHashes({ ja: 'J' });
    const d = deferred<{ hash: string; messages: Record<string, string> }>();
    api().mockReturnValue(d.promise);
    const i18n = newI18n();
    let result: string[] | undefined;
    void ensureLocaleMessages(i18n, ['ja'], { budgetMs: 3000 }).then((r) => { result = r; });
    await vi.advanceTimersByTimeAsync(2999);
    expect(result).toBeUndefined();
    await vi.advanceTimersByTimeAsync(1);
    expect(result).toEqual([]);
    d.resolve({ hash: 'J', messages: ja });
    await vi.advanceTimersByTimeAsync(0);
    expect(hasUiMessages(i18n, 'ja')).toBe(true);
  });
});

describe('refreshLoadedMessages 與版本協調', () => {
  it('已載入的語系雜湊變了就重取並換上；沒載入過的不取；目前語系另寫快取', async () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    i18n.global.locale.value = 'th';
    const get = api().mockResolvedValue({ hash: 'B', messages: { ...th, 'menu.home': 'ใหม่' } });
    await refreshLoadedMessages(i18n, [info('th', 'B'), info('vi', 'V')]);
    expect(get).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledWith('th', 'B');
    expect(i18n.global.t('menu.home')).toBe('ใหม่');
    expect(readUiCache()).toMatchObject({ code: 'th', hash: 'B' });
  });
  it('雜湊沒變不取', async () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    const get = api();
    await refreshLoadedMessages(i18n, [info('th', 'A')]);
    expect(get).not.toHaveBeenCalled();
  });
  it('(a) 打到舊台回 A 不裝，下一輪再取到 B 才裝', async () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    const get = api().mockResolvedValueOnce({ hash: 'A', messages: th }).mockResolvedValueOnce({ hash: 'B', messages: { ...th, 'menu.home': 'B' } });
    await refreshLoadedMessages(i18n, [info('th', 'B')]);
    expect(loadedHash('th')).toBe('A');
    await refreshLoadedMessages(i18n, [info('th', 'B')]);
    expect(get).toHaveBeenCalledTimes(2);
    expect(loadedHash('th')).toBe('B');
    expect(i18n.global.t('menu.home', {}, { locale: 'th' })).toBe('B');
  });
  it('(b) 第一次失敗，第二次成功就裝上', async () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    api().mockRejectedValueOnce(new Error('x')).mockResolvedValueOnce({ hash: 'B', messages: th });
    await refreshLoadedMessages(i18n, [info('th', 'B')]);
    expect(loadedHash('th')).toBe('A');
    await refreshLoadedMessages(i18n, [info('th', 'B')]);
    expect(loadedHash('th')).toBe('B');
  });
  it('(c) 舊請求晚到不覆蓋新版', async () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    const b = deferred<{ hash: string; messages: Record<string, string> }>();
    const c = deferred<{ hash: string; messages: Record<string, string> }>();
    const get = api().mockReturnValueOnce(b.promise).mockReturnValueOnce(c.promise);
    const first = refreshLoadedMessages(i18n, [info('th', 'B')]);
    const second = refreshLoadedMessages(i18n, [info('th', 'C')]);
    expect(get).toHaveBeenCalledTimes(2);
    c.resolve({ hash: 'C', messages: { ...th, 'menu.home': 'C' } });
    await second;
    b.resolve({ hash: 'B', messages: { ...th, 'menu.home': 'B' } });
    await first;
    await flushPromises();
    expect(loadedHash('th')).toBe('C');
    expect(i18n.global.t('menu.home', {}, { locale: 'th' })).toBe('C');
  });
  it('同語系、同目標雜湊的請求合併', async () => {
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    const b = deferred<{ hash: string; messages: Record<string, string> }>();
    const get = api().mockReturnValue(b.promise);
    const p = Promise.all([refreshLoadedMessages(i18n, [info('th', 'B')]), refreshLoadedMessages(i18n, [info('th', 'B')])]);
    expect(get).toHaveBeenCalledTimes(1);
    b.resolve({ hash: 'B', messages: th });
    await p;
    expect(loadedHash('th')).toBe('B');
  });
  it('(d) 超過逾時視為失敗，下一輪會重發', async () => {
    vi.useFakeTimers();
    const i18n = newI18n();
    installUiMessages(i18n, 'th', th, 'A');
    const get = api().mockReturnValueOnce(new Promise(() => {})).mockResolvedValueOnce({ hash: 'B', messages: th });
    const first = refreshLoadedMessages(i18n, [info('th', 'B')]);
    await vi.advanceTimersByTimeAsync(UI_FETCH_TIMEOUT_MS);
    await first;
    expect(loadedHash('th')).toBe('A');
    await refreshLoadedMessages(i18n, [info('th', 'B')]);
    expect(get).toHaveBeenCalledTimes(2);
    expect(loadedHash('th')).toBe('B');
  });
  it('還沒有任何文字的語系：回應雜湊與目標不同也先裝上', async () => {
    withHashes({ ja: 'J2' });
    api().mockResolvedValue({ hash: 'J1', messages: ja });
    const i18n = newI18n();
    expect(await ensureLocaleMessages(i18n, ['ja'])).toEqual(['ja']);
    expect(loadedHash('ja')).toBe('J1');
  });
});

describe('瀏覽器快取', () => {
  it('只存一份：寫 th 再寫 vi，讀回 vi', () => {
    writeUiCache('th', 'A', th);
    writeUiCache('vi', 'V', { 'menu.home': 'Trang chủ' });
    expect(readUiCache()).toEqual({ code: 'vi', hash: 'V', messages: { 'menu.home': 'Trang chủ' } });
    expect(Object.keys(localStorage).filter((k) => k.startsWith('formwright.ui'))).toEqual([UI_CACHE_KEY]);
  });
  it('setItem 丟 QuotaExceededError 不丟例外', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('full', 'QuotaExceededError'); });
    expect(() => writeUiCache('th', 'A', th)).not.toThrow();
  });
  it('形狀不符讀回 null', () => {
    localStorage.setItem(UI_CACHE_KEY, JSON.stringify({ code: 'th', hash: 'A', messages: 'x' }));
    expect(readUiCache()).toBeNull();
    localStorage.setItem(UI_CACHE_KEY, '{bad json');
    expect(readUiCache()).toBeNull();
  });
  it('localStorage 不可用回 null', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(readUiCache()).toBeNull();
  });
});
