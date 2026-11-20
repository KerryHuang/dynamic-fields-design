import { afterEach, describe, expect, it } from 'vitest';
import cases from '../../../../shared/locale-chain-cases.json';
import { BUILTIN_LOCALES } from 'src/i18n/locale';
import { setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';
import { compactI18n, missingRequired, resolveI18n, sameI18n } from './i18n-text';

afterEach(() => {
  setLocaleInfos(BUILTIN_LOCALES);
  setLocaleSettings({ defaultLocale: 'zh-TW', requiredText: ['zh-TW'] });
});

describe('resolveI18n 跑共用案例（與後端 I18nText.Resolve 同結果）', () => {
  for (const c of cases.cases) {
    it(c.name, () => {
      setLocaleInfos(c.installed.map((l) => ({ code: l.code, nativeName: l.code, quasarLang: null, fallbackCode: l.fallback })));
      setLocaleSettings({ defaultLocale: c.default });
      expect(resolveI18n(c.text, c.locale)).toBe(c.expected);
    });
  }
});

describe('resolveI18n', () => {
  it('returns the requested locale when present', () => {
    expect(resolveI18n({ 'zh-TW': '客戶', 'en-US': 'Customer' }, 'en-US')).toBe('Customer');
  });
  it('falls back to zh-TW for missing locale', () => {
    expect(resolveI18n({ 'zh-TW': '客戶' }, 'th')).toBe('客戶');
  });
  it('falls back for empty string', () => {
    expect(resolveI18n({ 'zh-TW': '客戶', 'en-US': '' }, 'en-US')).toBe('客戶');
  });
  it('returns empty string when nothing is available', () => {
    expect(resolveI18n({}, 'vi')).toBe('');
  });
  it('沒有繁中時用第一個有值的語系', () => {
    expect(resolveI18n({ 'en-US': 'Order' }, 'th')).toBe('Order');
  });
  it('預設語系在繁中之前', () => {
    setLocaleSettings({ defaultLocale: 'en-US' });
    expect(resolveI18n({ 'zh-TW': '訂單', 'en-US': 'Order' }, 'th')).toBe('Order');
  });
});

describe('compactI18n', () => {
  it('去空白，沒有繁中也保留其他語系', () => {
    expect(compactI18n({ 'en-US': ' Order ' })).toEqual({ 'en-US': 'Order' });
  });
  it('全部空白回 undefined', () => {
    expect(compactI18n({ 'zh-TW': ' ', 'en-US': '' })).toBeUndefined();
  });
});

describe('missingRequired', () => {
  it('新增（沒有 original）照必填語系擋', () => {
    setLocaleSettings({ requiredText: ['zh-TW', 'en-US'] });
    expect(missingRequired({ 'zh-TW': '訂單' })).toEqual(['en-US']);
    expect(missingRequired({ 'zh-TW': '訂單', 'en-US': 'Order' })).toEqual([]);
  });
  it('編輯沒改的既有資料不擋，改了才擋', () => {
    setLocaleSettings({ requiredText: ['zh-TW', 'en-US'] });
    const original = { 'zh-TW': '訂單' };
    expect(missingRequired({ 'zh-TW': ' 訂單 ' }, original)).toEqual([]);
    expect(missingRequired({ 'zh-TW': '訂單二' }, original)).toEqual(['en-US']);
  });
  it('sameI18n 忽略空值與空白', () => {
    expect(sameI18n({ 'zh-TW': 'a', th: '' }, { 'zh-TW': 'a ' })).toBe(true);
    expect(sameI18n({ 'zh-TW': 'a' }, { 'zh-TW': 'b' })).toBe(false);
  });
});

describe('resolveI18n 最後一層固定照支援語系順序', () => {
  it('預設英文、要求繁中、只有 vi 與 th：不論插入順序都是 th', () => {
    setLocaleSettings({ defaultLocale: 'en-US' });
    expect(resolveI18n({ vi: '越', th: '泰' }, 'zh-TW')).toBe('泰');
    expect(resolveI18n({ th: '泰', vi: '越' }, 'zh-TW')).toBe('泰');
  });
});
