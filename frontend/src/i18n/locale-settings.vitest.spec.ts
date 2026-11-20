import { describe, expect, it } from 'vitest';
import { applyLocalePolicies, defaultLocale, enabledLocales, requiredTextLocales, setLocaleSettings, toLocaleCodes } from './locale-settings';

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
});
