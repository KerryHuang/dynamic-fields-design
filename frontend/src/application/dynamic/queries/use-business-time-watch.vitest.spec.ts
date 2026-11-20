import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n } from 'vue-i18n';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import { BUILTIN_LOCALES } from 'src/i18n/locale';
import { enabledLocales, installedLocales, localeChain, nativeName, requiredTextLocales, setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { installUiMessages, loadedHash, readUiCache, resetUiMessagesForTests } from 'src/i18n/ui-messages';
import { useBusinessTimeWatch } from './use-business-time-watch';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getEffectivePolicies: vi.fn(), getUiMessages: vi.fn() } }));
vi.mock('quasar', async (importOriginal) => ({ ...(await importOriginal<typeof import('quasar')>()), Dialog: { create: vi.fn() } }));
const api = vi.mocked(dynamicApi);

let reload: ReturnType<typeof vi.fn>;
const create = vi.mocked(quasar.Dialog.create);
const original = window.location;

function mountWatch(qc = new QueryClient(), i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })) {
  const Host = defineComponent({ setup: () => { useBusinessTimeWatch(); return () => h('i'); } });
  mount(Host, {
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: qc }]] },
  });
  return qc;
}

describe('useBusinessTimeWatch', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    reload = vi.fn();
    Object.defineProperty(window, 'location', { configurable: true, value: { ...original, reload } });
    create.mockImplementation(() => {
      const chain = { onOk: (cb: () => void) => { cb(); return chain; } };
      return chain as never;
    });
  });
  afterEach(() => {
    Object.defineProperty(window, 'location', { configurable: true, value: original });
    setBusinessOffset('+08:00');
    setLocaleInfos(BUILTIN_LOCALES);
    setLocaleSettings({ defaultLocale: 'zh-TW', enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] });
  });

  it('時區沒變不提示', async () => {
    api.getEffectivePolicies.mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+08:00' } });
    mountWatch();
    await flushPromises();
    expect(api.getEffectivePolicies).toHaveBeenCalledWith(null);
    expect(create).not.toHaveBeenCalled();
  });

  it('時區變了先換偏移再要求重新整理', async () => {
    api.getEffectivePolicies.mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+07:00' } });
    let offsetWhenPrompted = '';
    create.mockImplementation(() => {
      offsetWhenPrompted = businessOffset();
      const chain = { onOk: (cb: () => void) => { cb(); return chain; } };
      return chain as never;
    });
    mountWatch();
    await flushPromises();
    expect(offsetWhenPrompted).toBe('+07:00');
    const opts = create.mock.calls[0]![0] as { message: string; persistent: boolean; cancel: boolean };
    expect(opts.persistent).toBe(true);
    expect(opts.cancel).toBe(false);
    expect(opts.message).toContain('UTC+07:00');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('同一個新值只提示一次', async () => {
    api.getEffectivePolicies.mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+07:00' } });
    const qc = mountWatch();
    await flushPromises();
    // 還沒重新整理前再抓到同一個值（即使本頁的偏移又被改掉）：不再提示
    setBusinessOffset('+08:00');
    api.getEffectivePolicies.mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+07:00' } });
    await qc.refetchQueries({ queryKey: ['effective-policies', ''] });
    await flushPromises();
    expect(create).toHaveBeenCalledTimes(1);
  });

  it('語系政策變了只更新設定，不提示', async () => {
    api.getEffectivePolicies.mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+08:00', 'locale.enabled': ['zh-TW', 'en-US'], 'locale.requiredText': ['zh-TW', 'en-US'] } });
    mountWatch();
    await flushPromises();
    expect(enabledLocales()).toEqual(['zh-TW', 'en-US']);
    expect(requiredTextLocales()).toEqual(['zh-TW', 'en-US']);
    expect(create).not.toHaveBeenCalled();
  });

  it('生效值多了新裝的語系：語系資訊跟著換、不提示', async () => {
    const ja = { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US', uiHash: '' };
    api.getEffectivePolicies.mockResolvedValue({ values: { 'locale.timeZone': '+08:00', 'locale.enabled': ['zh-TW', 'ja'] }, locales: [...BUILTIN_LOCALES, ja] });
    mountWatch();
    await flushPromises();
    expect(installedLocales()).toEqual(['zh-TW', 'en-US', 'th', 'vi', 'ja']);
    expect(enabledLocales()).toEqual(['zh-TW', 'ja']);
    expect(create).not.toHaveBeenCalled();
  });

  it('政策值不變、只有語系資訊變（新增、改自稱、改上層語系）也跟著換', async () => {
    const values = { 'locale.timeZone': '+08:00' };
    api.getEffectivePolicies.mockResolvedValue({ values, locales: BUILTIN_LOCALES });
    const qc = mountWatch();
    await flushPromises();
    expect(installedLocales()).not.toContain('ja');

    const refetch = async (ja: { nativeName: string; fallbackCode: string | null }) => {
      api.getEffectivePolicies.mockResolvedValue({ values, locales: [...BUILTIN_LOCALES, { code: 'ja', quasarLang: 'ja', uiHash: '', ...ja }] });
      await qc.refetchQueries({ queryKey: ['effective-policies', ''] });
      await flushPromises();
    };
    await refetch({ nativeName: '日本語', fallbackCode: null });
    expect(installedLocales()).toContain('ja');
    await refetch({ nativeName: 'にほんご', fallbackCode: null });
    expect(nativeName('ja')).toBe('にほんご');
    expect(localeChain('ja')[0]).toBe('zh-TW');
    await refetch({ nativeName: 'にほんご', fallbackCode: 'en-US' });
    expect(localeChain('ja')[0]).toBe('en-US');
  });

  describe('介面文字雜湊改變時背景換新（i4b 判斷 15）', () => {
    const values = { 'locale.timeZone': '+08:00' };
    const withTh = (uiHash: string) => ({ values, locales: BUILTIN_LOCALES.map((l) => (l.code === 'th' ? { ...l, uiHash } : l)) });
    // 目前語系 th、已裝上雜湊 A
    const thLoaded = () => {
      const i18n = createI18n({ legacy: false, locale: 'th', fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } });
      installUiMessages(i18n, 'th', { 'menu.home': 'บ้าน-A' }, 'A');
      return i18n;
    };
    const refetch = async (qc: QueryClient) => {
      vi.setSystemTime(Date.now() + 60_000); // 下一輪（60 秒後），dataUpdatedAt 一定不同
      await qc.refetchQueries({ queryKey: ['effective-policies', ''] });
      await flushPromises();
    };
    beforeEach(() => { resetUiMessagesForTests(); localStorage.clear(); vi.useFakeTimers({ toFake: ['Date'] }); });
    afterEach(() => { vi.useRealTimers(); });

    it('雜湊變了：取新版、換上新句子、寫快取，不提示', async () => {
      const i18n = thLoaded();
      api.getEffectivePolicies.mockResolvedValue(withTh('B'));
      api.getUiMessages.mockResolvedValue({ hash: 'B', messages: { 'menu.home': 'บ้าน-B' } });
      mountWatch(new QueryClient(), i18n);
      await flushPromises();
      expect(api.getUiMessages).toHaveBeenCalledWith('th', 'B');
      expect(i18n.global.t('menu.home')).toBe('บ้าน-B');
      expect(readUiCache()).toMatchObject({ code: 'th', hash: 'B' });
      expect(create).not.toHaveBeenCalled();
    });

    it('雜湊沒變不取', async () => {
      const i18n = thLoaded();
      api.getEffectivePolicies.mockResolvedValue(withTh('A'));
      mountWatch(new QueryClient(), i18n);
      await flushPromises();
      expect(api.getUiMessages).not.toHaveBeenCalled();
    });

    it('政策值不變（同一個參考）、只有 uiHash 變也換', async () => {
      const i18n = thLoaded();
      api.getEffectivePolicies.mockResolvedValue(withTh('A'));
      const qc = mountWatch(new QueryClient(), i18n);
      await flushPromises();
      api.getEffectivePolicies.mockResolvedValue(withTh('B'));
      api.getUiMessages.mockResolvedValue({ hash: 'B', messages: { 'menu.home': 'บ้าน-B' } });
      await refetch(qc);
      expect(i18n.global.t('menu.home')).toBe('บ้าน-B');
    });

    it.each([
      ['文字請求失敗', () => api.getUiMessages.mockRejectedValueOnce(new Error('down'))],
      ['打到舊台回 A', () => api.getUiMessages.mockResolvedValueOnce({ hash: 'A', messages: { 'menu.home': 'บ้าน-A' } })],
    ])('連回兩次 B（%s）：第二輪回應完全相同也會重取，回 B 後換上', async (_, firstRound) => {
      const i18n = thLoaded();
      const same = withTh('B');
      api.getEffectivePolicies.mockResolvedValue(same);
      firstRound();
      const qc = mountWatch(new QueryClient(), i18n);
      await flushPromises();
      expect(i18n.global.t('menu.home')).toBe('บ้าน-A');
      expect(loadedHash('th')).toBe('A');
      api.getUiMessages.mockResolvedValue({ hash: 'B', messages: { 'menu.home': 'บ้าน-B' } });
      await refetch(qc);
      expect(api.getUiMessages).toHaveBeenCalledTimes(2);
      expect(i18n.global.t('menu.home')).toBe('บ้าน-B');
    });
  });
});
