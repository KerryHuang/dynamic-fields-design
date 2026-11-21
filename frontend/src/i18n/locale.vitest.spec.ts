import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import enUS from 'shared/i18n/en-US/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import { setLocaleInfos, setLocaleSettings } from './locale-settings';
import { BUILTIN_LOCALES, LOCALE_KEY, applyDocumentLocale, applyLocale, quasarPacks, readLocale } from './locale';
import { PREVIEW_KEY } from './locale-preview';
import { endPreview, readUiCache, refreshLoadedMessages, resetUiMessagesForTests, writeUiCache } from './ui-messages';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { createPinia, setActivePinia } from 'pinia';

const NAME = { 'zh-TW': '訂單', 'en-US': 'Orders', th: 'คำสั่งซื้อ' };

beforeEach(() => { localStorage.clear(); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('readLocale', () => {
  it('第一次開啟沒記過語系，回 null 讓呼叫端套用預設語系', () => {
    expect(readLocale()).toBeNull();
  });
  it('重開讀回上次的選擇', () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    expect(readLocale()).toBe('th');
  });
  it('不對照寫死清單：存什麼讀回什麼（是否啟用由 auth guard 判斷）', () => {
    localStorage.setItem(LOCALE_KEY, 'fr');
    expect(readLocale()).toBe('fr');
    localStorage.setItem(LOCALE_KEY, 'ja');
    expect(readLocale()).toBe('ja');
  });
  it('只有空白回 null', () => {
    localStorage.setItem(LOCALE_KEY, '   ');
    expect(readLocale()).toBeNull();
  });
  it('localStorage 讀取丟例外時回 null', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(readLocale()).toBeNull();
  });
});

describe('applyLocale', () => {
  it('選擇後寫入 localStorage，並更新 <html lang>', () => {
    const locale = { value: 'zh-TW' };
    applyLocale('vi', locale);
    expect(localStorage.getItem(LOCALE_KEY)).toBe('vi');
    expect(locale.value).toBe('vi');
    expect(document.documentElement.lang).toBe('vi');
  });
  it('localStorage 寫入丟例外時仍然切換成功', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    const locale = { value: 'zh-TW' };
    expect(() => applyLocale('en-US', locale)).not.toThrow();
    expect(locale.value).toBe('en-US');
  });
  it('切換後 resolveI18n 的顯示跟著變', () => {
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    expect(resolveI18n(NAME, i18n.global.locale.value)).toBe('訂單');
    applyLocale('en-US', i18n.global.locale);
    expect(resolveI18n(NAME, i18n.global.locale.value)).toBe('Orders');
    applyLocale('vi', i18n.global.locale);
    expect(resolveI18n(NAME, i18n.global.locale.value)).toBe('訂單'); // vi 沒填，退回繁中
  });
  it('Quasar 語言包跟著換', async () => {
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyLocale('th', { value: 'zh-TW' });
    await vi.waitFor(() => expect(setLang.mock.calls.map((c) => c[0].isoName)).toContain('th'));
  });
});

describe('applyDocumentLocale（Quasar 語言包全開）', () => {
  const pack = (code: string) => `../../node_modules/quasar/lang/${code}.js`;
  const saved = { ...quasarPacks };
  afterEach(() => {
    for (const k of Object.keys(quasarPacks)) delete quasarPacks[k];
    Object.assign(quasarPacks, saved);
    setLocaleInfos(BUILTIN_LOCALES);
  });
  const lastSet = (spy: { mock: { calls: { 0: { isoName: string } }[] } }) => spy.mock.calls.at(-1)?.[0].isoName;

  it('新裝的語系依 quasarLang 載入對應的語言包', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: null, uiHash: '' }]);
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('ja');
    expect(document.documentElement.lang).toBe('ja');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('ja'));
  });
  it('沒有 Quasar 語言包（quasarLang: null）時載入 en-US', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'xx', nativeName: 'X', quasarLang: null, fallbackCode: null, uiHash: '' }]);
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('xx');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('en-US'));
  });
  it('語言包載入失敗改載 en-US', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: null, uiHash: '' }]);
    quasarPacks[pack('ja')] = () => Promise.reject(new Error('chunk failed'));
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('ja');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('en-US'));
  });
  // 模擬 Quasar 的 Lang.set：會把 <html lang> 寫成語言包的 isoName（#73）
  const quasarLikeSet = () => vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation((p: { isoName: string }) => { document.documentElement.lang = p.isoName; });
  it('#73 載入完語言包後 <html lang> 仍是使用者的語系（ja 有 Quasar 包）', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: null, uiHash: '' }]);
    const setLang = quasarLikeSet();
    applyDocumentLocale('ja');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('ja'));
    expect(document.documentElement.lang).toBe('ja');
  });
  it('#73 沒有 Quasar 語言包退回 en-US 載入，<html lang> 仍是 xx', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'xx', nativeName: 'X', quasarLang: null, fallbackCode: null, uiHash: '' }]);
    const setLang = quasarLikeSet();
    applyDocumentLocale('xx');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('en-US'));
    expect(document.documentElement.lang).toBe('xx');
  });
  it('#73 執行期從 zh-TW 切到 ja 再切 vi：每次載完 <html lang> 都是當下語系', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: null, fallbackCode: 'en-US', uiHash: '' }]);
    const setLang = quasarLikeSet();
    const locale = { value: 'zh-TW' };
    applyLocale('ja', locale, false);
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('en-US'));
    expect(document.documentElement.lang).toBe('ja');
    applyLocale('vi', locale, false);
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('vi'));
    expect(document.documentElement.lang).toBe('vi');
  });
  it('快速連切：較早的載入較晚完成也不蓋掉後選的語言', async () => {
    let releaseTh!: () => void;
    const th = saved[pack('th')]!;
    quasarPacks[pack('th')] = () => new Promise((resolve) => { releaseTh = () => resolve(th()); });
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('th');
    applyDocumentLocale('vi');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('vi'));
    releaseTh();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(lastSet(setLang)).toBe('vi');
  });
});

describe('LocaleSwitcher', () => {
  it('選單列出四種語言的自稱，點選後立即切換並記住', async () => {
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW, 'en-US': enUS } });
    const w = mount(LocaleSwitcher, {
      attachTo: document.body,
      global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] },
    });
    (document.body.querySelector('[data-testid="locale-open"]') as HTMLElement).click();
    await flushPromises();
    const items = [...document.body.querySelectorAll('[data-testid^="locale-option-"]')].map((e) => e.textContent?.trim());
    expect(items).toEqual(['繁體中文', 'English', 'ไทย', 'Tiếng Việt']);
    (document.body.querySelector('[data-testid="locale-option-en-US"]') as HTMLElement).click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('en-US');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('en-US');
    w.unmount();
  });

  it('只列啟用的語系', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(LocaleSwitcher, {
      attachTo: document.body,
      global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] },
    });
    (document.body.querySelector('[data-testid="locale-open"]') as HTMLElement).click();
    await flushPromises();
    expect([...document.body.querySelectorAll('[data-testid^="locale-option-"]')].map((e) => e.textContent?.trim())).toEqual(['繁體中文', 'English']);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'] });
    w.unmount();
  });
});

describe('LocaleSwitcher 新裝的語系', () => {
  it('裝了 ja 且啟用：多一項「日本語」，點選後切換', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US', uiHash: '' }]);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi', 'ja'] });
    // i4b 起切換前先載入文字（前端只打包繁中）
    vi.spyOn(dynamicApi, 'getUiMessages').mockResolvedValue({ hash: '', messages: { 'menu.home': 'ホーム' } });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(LocaleSwitcher, { attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] } });
    (document.body.querySelector('[data-testid="locale-open"]') as HTMLElement).click();
    await flushPromises();
    expect([...document.body.querySelectorAll('[data-testid^="locale-option-"]')].map((e) => e.textContent?.trim()))
      .toEqual(['繁體中文', 'English', 'ไทย', 'Tiếng Việt', '日本語']);
    (document.body.querySelector('[data-testid="locale-option-ja"]') as HTMLElement).click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('ja');
    setLocaleInfos(BUILTIN_LOCALES);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'] });
    w.unmount();
  });
});

describe('LocaleSwitcher 掛載後政策更新', () => {
  it('啟用語系變了，已掛載的選單跟著列新的', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'] });
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(LocaleSwitcher, { attachTo: document.body, global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] } });
    (document.body.querySelector('[data-testid="locale-open"]') as HTMLElement).click();
    await flushPromises();
    expect(document.body.querySelectorAll('[data-testid^="locale-option-"]')).toHaveLength(4);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'] });
    await flushPromises();
    expect(document.body.querySelectorAll('[data-testid^="locale-option-"]')).toHaveLength(2);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'] });
    w.unmount();
  });
});

describe('LocaleSwitcher 載入文字與試看（i4b 判斷 17、19）', () => {
  const JA = { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US', uiHash: 'J' };
  const zhOnly = () => createI18n({ legacy: false, locale: 'zh-TW', fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const plugins = (i18n: ReturnType<typeof zhOnly>) => ({ plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n] as never });
  const el = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const open = async () => { el('locale-open')!.click(); await flushPromises(); };
  beforeEach(() => { resetUiMessagesForTests(); sessionStorage.clear(); setActivePinia(createPinia()); });
  afterEach(() => {
    setLocaleInfos(BUILTIN_LOCALES);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'] });
    document.body.innerHTML = '';
  });

  it('沒傳 allowPreview：只有啟用語系、沒有試看項目，元件本身不查身分', async () => {
    const me = vi.spyOn(dynamicApi, 'getMe');
    mount(LocaleSwitcher, { attachTo: document.body, global: plugins(zhOnly()) });
    setLocaleInfos([...BUILTIN_LOCALES, JA]);
    await open();
    expect([...document.body.querySelectorAll('[data-testid^="locale-option-"]')].map((e) => e.textContent?.trim()))
      .toEqual(['繁體中文', 'English', 'ไทย', 'Tiếng Việt']);
    expect(document.body.querySelector('[data-testid^="locale-preview-"]')).toBeNull();
    expect(me).not.toHaveBeenCalled();
  });

  it('選 th：載入中有 spinner，完成後切換並記住', async () => {
    let release!: () => void;
    vi.spyOn(dynamicApi, 'getUiMessages').mockImplementation(() => new Promise((r) => { release = () => r({ hash: '', messages: { 'menu.home': 'หน้าแรก' } }); }));
    const i18n = zhOnly();
    mount(LocaleSwitcher, { attachTo: document.body, global: plugins(i18n) });
    await open();
    el('locale-option-th')!.click();
    await flushPromises();
    expect(el('locale-option-th')!.querySelector(`[aria-label="${zhTW.locale.loading}"]`)).not.toBeNull();
    expect(i18n.global.locale.value).toBe('zh-TW');
    release();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('th');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('th');
  });

  it('載入失敗：提示「無法載入ไทย」，停在原語系、不記住', async () => {
    vi.spyOn(dynamicApi, 'getUiMessages').mockRejectedValue(new Error('down'));
    const i18n = zhOnly();
    mount(LocaleSwitcher, { attachTo: document.body, global: plugins(i18n) });
    const notify = vi.spyOn(quasar.Notify, 'create').mockImplementation(() => () => {});
    await open();
    el('locale-option-th')!.click();
    await flushPromises();
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ message: '無法載入ไทย，請稍後再試' }));
    expect(i18n.global.locale.value).toBe('zh-TW');
    expect(localStorage.getItem(LOCALE_KEY)).toBeNull();
  });

  it('allowPreview：已安裝未啟用的 ja 列在分隔線後；選它只試看，再選 English 結束試看並記住', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, JA]);
    vi.spyOn(dynamicApi, 'getUiMessages').mockImplementation(async (code) => ({ hash: code === 'ja' ? 'J' : '', messages: { 'menu.home': code } }));
    const i18n = zhOnly();
    mount(LocaleSwitcher, { attachTo: document.body, props: { allowPreview: true }, global: plugins(i18n) });
    await open();
    expect(el('locale-preview-separator')).not.toBeNull();
    expect(el('locale-preview-ja')!.textContent?.trim()).toBe(`日本語（${zhTW.locale.notEnabled}）`);
    el('locale-preview-ja')!.click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('ja');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBe('ja');
    expect(localStorage.getItem(LOCALE_KEY)).toBeNull();
    expect(readUiCache()?.code).not.toBe('ja');
    await open();
    el('locale-option-en-US')!.click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('en-US');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
    expect(localStorage.getItem(LOCALE_KEY)).toBe('en-US');
  });

  it('試看 ja 中再切 th 失敗：仍標記在試看、背景換新不寫快取、結束試看回原語系（i4b 修正輪 2）', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, JA]);
    vi.spyOn(dynamicApi, 'getUiMessages').mockImplementation(async (code) => {
      if (code === 'th') throw new Error('down');
      return { hash: code === 'ja' ? 'J' : '', messages: { 'menu.home': code } };
    });
    vi.spyOn(quasar.Notify, 'create').mockImplementation(() => () => {});
    const i18n = zhOnly();
    mount(LocaleSwitcher, { attachTo: document.body, props: { allowPreview: true }, global: plugins(i18n) });
    await open();
    el('locale-preview-ja')!.click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('ja');
    await open();
    el('locale-option-th')!.click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('ja');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBe('ja');
    // 60 秒換新：ja 有新版
    vi.mocked(dynamicApi.getUiMessages).mockResolvedValue({ hash: 'J2', messages: { 'menu.home': 'ja2' } });
    await refreshLoadedMessages(i18n, [...BUILTIN_LOCALES, { ...JA, uiHash: 'J2' }]);
    expect(i18n.global.t('menu.home')).toBe('ja2');
    expect(readUiCache()?.code).not.toBe('ja');
    // 登出等結束試看：回原語系
    endPreview(i18n);
    await flushPromises();
    expect(i18n.global.locale.value).toBe('zh-TW');
    expect(sessionStorage.getItem(PREVIEW_KEY)).toBeNull();
  });

  it('試看標記寫不進 sessionStorage：不切換、不寫一般快取（i4b 修正輪短核對）', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, JA]);
    vi.spyOn(dynamicApi, 'getUiMessages').mockImplementation(async (code) => ({ hash: code === 'ja' ? 'J' : '', messages: { 'menu.home': code } }));
    const i18n = zhOnly();
    mount(LocaleSwitcher, { attachTo: document.body, props: { allowPreview: true }, global: plugins(i18n) });
    writeUiCache('zh-TW', 'Z', { 'menu.home': '首頁' });
    const setItem = Storage.prototype.setItem;
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, k: string, v: string) {
      if (this === sessionStorage) throw new DOMException('full', 'QuotaExceededError');
      setItem.call(this, k, v);
    });
    await open();
    el('locale-preview-ja')!.click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('zh-TW');
    expect(readUiCache()?.code).toBe('zh-TW');
  });

  it('allowPreview 但沒有未啟用語系：沒有分隔線', async () => {
    mount(LocaleSwitcher, { attachTo: document.body, props: { allowPreview: true }, global: plugins(zhOnly()) });
    await open();
    expect(el('locale-preview-separator')).toBeNull();
  });
});

describe('BUILTIN_LOCALES', () => {
  it('來自 shared/i18n/<code>/locale.json，依 order 排成內建四語的順序，內容不變', () => {
    expect(BUILTIN_LOCALES.map((l) => l.code)).toEqual(['zh-TW', 'en-US', 'th', 'vi']);
    expect(BUILTIN_LOCALES).toEqual([
      { code: 'zh-TW', nativeName: '繁體中文', quasarLang: 'zh-TW', fallbackCode: null, uiHash: '' },
      { code: 'en-US', nativeName: 'English', quasarLang: 'en-US', fallbackCode: null, uiHash: '' },
      { code: 'th', nativeName: 'ไทย', quasarLang: 'th', fallbackCode: null, uiHash: '' },
      { code: 'vi', nativeName: 'Tiếng Việt', quasarLang: 'vi', fallbackCode: null, uiHash: '' },
    ]);
  });
});
