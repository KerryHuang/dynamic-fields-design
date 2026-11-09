import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import { LOCALE_KEY, LOCALES, applyLocale, readLocale } from './locale';

const NAME = { 'zh-TW': '訂單', 'en-US': 'Orders', th: 'คำสั่งซื้อ' };

beforeEach(() => { localStorage.clear(); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('readLocale', () => {
  it('第一次開啟固定繁中', () => {
    expect(readLocale()).toBe('zh-TW');
  });
  it('重開讀回上次的選擇', () => {
    localStorage.setItem(LOCALE_KEY, 'th');
    expect(readLocale()).toBe('th');
  });
  it('存了不認得的值時退回繁中', () => {
    localStorage.setItem(LOCALE_KEY, 'fr');
    expect(readLocale()).toBe('zh-TW');
  });
  it('localStorage 讀取丟例外時退回繁中', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(readLocale()).toBe('zh-TW');
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
    expect(items).toEqual(LOCALES.map((l) => l.label));
    expect(items).toEqual(['繁體中文', 'English', 'ไทย', 'Tiếng Việt']);
    (document.body.querySelector('[data-testid="locale-option-en-US"]') as HTMLElement).click();
    await flushPromises();
    expect(i18n.global.locale.value).toBe('en-US');
    expect(localStorage.getItem(LOCALE_KEY)).toBe('en-US');
    w.unmount();
  });
});
