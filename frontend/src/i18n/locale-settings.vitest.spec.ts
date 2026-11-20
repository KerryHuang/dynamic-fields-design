import { afterEach, describe, expect, it } from 'vitest';
import cases from '../../../shared/locale-chain-cases.json';
import { BUILTIN_LOCALES } from './locale';
import {
  applyLocalePolicies, defaultLocale, enabledLocales, installedLocales, localeChain, localeInfo, nativeName, requiredTextLocales,
  setLocaleInfos, setLocaleSettings, toLocaleCodes, type LocaleInfo,
} from './locale-settings';

const JA: LocaleInfo = { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US', uiHash: '' };

afterEach(() => {
  setLocaleInfos(BUILTIN_LOCALES);
  setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] });
});

describe('locale-settings', () => {
  it('讀回設定的值（vitest.setup 已設出廠值）', () => {
    setLocaleSettings({ defaultLocale: 'en-US', enabled: ['en-US', 'th'], requiredText: ['en-US'] });
    expect(defaultLocale()).toBe('en-US');
    expect(enabledLocales()).toEqual(['en-US', 'th']);
    expect(requiredTextLocales()).toEqual(['en-US']);
    setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] });
  });

  it('applyLocalePolicies 從全系統生效值更新，缺的或不認得的項目不動', () => {
    applyLocalePolicies({ 'locale.default': 'th', 'locale.enabled': ['th', 'fr', 'th', 'zh-TW'], 'locale.requiredText': 'bad' });
    expect(defaultLocale()).toBe('th');
    expect(enabledLocales()).toEqual(['th', 'zh-TW']);
    expect(requiredTextLocales()).toEqual(['zh-TW']);
    setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'] });
  });

  it('toLocaleCodes 去重並丟掉不認得的代碼', () => {
    expect(toLocaleCodes(['vi', 'x', 'vi'])).toEqual(['vi']);
    expect(toLocaleCodes(undefined)).toEqual([]);
  });

  it('setLocaleInfos 後依序列出已安裝語系，自稱找不到回代碼', () => {
    setLocaleInfos([...BUILTIN_LOCALES, JA]);
    expect(installedLocales()).toEqual(['zh-TW', 'en-US', 'th', 'vi', 'ja']);
    expect(nativeName('ja')).toBe('日本語');
    expect(nativeName('th')).toBe('ไทย');
    expect(nativeName('ko')).toBe('ko');
    expect(localeInfo('ja')?.fallbackCode).toBe('en-US');
  });

  it('toLocaleCodes 認語系資訊裡的代碼（含新裝的）', () => {
    setLocaleInfos([...BUILTIN_LOCALES, JA]);
    expect(toLocaleCodes(['ja', 'ko', 'zh-TW'])).toEqual(['ja', 'zh-TW']);
  });

  it('applyLocalePolicies 帶 locales 時換掉語系資訊，啟用清單只留在清單內的代碼', () => {
    applyLocalePolicies({ 'locale.enabled': ['zh-TW', 'ja', 'ko'] }, [...BUILTIN_LOCALES, JA]);
    expect(installedLocales()).toContain('ja');
    expect(enabledLocales()).toEqual(['zh-TW', 'ja']);
  });

  it('語系資訊還沒載入時退回鏈是「預設 → 繁中」', () => {
    setLocaleInfos([]);
    setLocaleSettings({ defaultLocale: 'en-US' });
    expect(localeChain('th')).toEqual(['en-US', 'zh-TW']);
  });
});

describe('localeChain 跑共用案例（與後端 LocaleChain 同一條鏈）', () => {
  for (const c of cases.cases) {
    it(c.name, () => {
      setLocaleInfos(c.installed.map((l) => ({ code: l.code, nativeName: l.code, quasarLang: null, fallbackCode: l.fallback, uiHash: '' })));
      setLocaleSettings({ defaultLocale: c.default });
      expect(localeChain(c.locale)).toEqual(c.fallbacks);
    });
  }
});
