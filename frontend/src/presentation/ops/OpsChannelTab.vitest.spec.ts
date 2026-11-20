import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { setLocaleSettings } from 'src/i18n/locale-settings';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import OpsChannelTab from './OpsChannelTab.vue';

const channel = (value: string, isFactory: boolean) => ({
  enabled: true, toAddresses: ['a@test.invalid'], mailLocale: { value, factory: 'en-US', isFactory },
  consecutiveFailures: 0, editable: true, mailConfigured: true,
});

async function mountTab(c: ReturnType<typeof channel>) {
  vi.spyOn(dynamicApi, 'getSmtpChannel').mockResolvedValue(c as never);
  const w = mount(OpsChannelTab, {
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } }), [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return w;
}
type Opt = { value: string | null; label: string; disable?: boolean };
const optionsOf = (w: Awaited<ReturnType<typeof mountTab>>) => w.findAllComponents({ name: 'QSelect' }).find((c) => c.props('label') === '信件語系')!.props('options') as Opt[];

afterEach(() => { vi.restoreAllMocks(); setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'] }); });

describe('OpsChannelTab 信件語言', () => {
  it('只列啟用語系，出廠項顯示預設語系', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'] });
    const w = await mountTab(channel('en-US', true));
    const o = optionsOf(w);
    expect(o.map((x) => x.value)).toEqual([null, 'zh-TW', 'en-US']);
    expect(o[0]!.label).toContain('English');
  });

  it('已存的語言之後被停用：仍列出並標已停用、不可新選', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'] });
    const w = await mountTab(channel('th', false));
    const o = optionsOf(w).find((x) => x.value === 'th')!;
    expect(o.label).toBe('ไทย（已停用）');
    expect(o.disable).toBe(true);
  });

  it('存檔不改信件語言時不送 mailLocale，已存語言停用也不會被後端擋', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'] });
    const save = vi.spyOn(dynamicApi, 'saveSmtpChannel').mockResolvedValue(undefined as never);
    const w = await mountTab(channel('th', false));
    await w.get('[data-testid="channel-save"]').trigger('click');
    await flushPromises();
    expect(save).toHaveBeenCalledOnce();
    expect(save.mock.calls[0]![0]).not.toHaveProperty('mailLocale');
  });
});
