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
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: null }]);
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('ja');
    expect(document.documentElement.lang).toBe('ja');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('ja'));
  });
  it('沒有 Quasar 語言包（quasarLang: null）時載入 en-US', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'xx', nativeName: 'X', quasarLang: null, fallbackCode: null }]);
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('xx');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('en-US'));
  });
  it('語言包載入失敗改載 en-US', async () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: null }]);
    quasarPacks[pack('ja')] = () => Promise.reject(new Error('chunk failed'));
    const setLang = vi.spyOn(quasar.Quasar.lang, 'set').mockImplementation(() => {});
    applyDocumentLocale('ja');
    await vi.waitFor(() => expect(lastSet(setLang)).toBe('en-US'));
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
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US' }]);
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi', 'ja'] });
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
