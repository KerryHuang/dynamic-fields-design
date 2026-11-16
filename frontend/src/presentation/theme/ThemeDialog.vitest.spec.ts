import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import { FACTORY_ME_THEME } from 'src/domain/theme/theme-fixtures';
import { useThemeStore } from 'src/stores/use-theme-store';

const select = vi.hoisted(() => vi.fn());
vi.mock('src/application/dynamic/queries/theme-queries', () => ({ useSelectTheme: () => select }));

import ThemeDialog from './ThemeDialog.vue';

const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;

function open() {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(ThemeDialog, {
    props: { modelValue: true },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], i18n] },
  });
  return flushPromises();
}

describe('ThemeDialog', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    select.mockReset();
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('選了立刻呼叫 useSelectTheme，另一欄維持目前的個人設定', async () => {
    useThemeStore().applyMe({ ...FACTORY_ME_THEME, style: 'airy', effectiveStyle: 'airy' });
    await open();
    q('style-industrial')!.click();
    await flushPromises();
    expect(select).toHaveBeenCalledTimes(1);
    expect(select.mock.calls[0]![0]).toEqual({ mode: null, style: 'industrial' });
  });

  it('失敗時顯示錯誤通知', async () => {
    await open();
    const notify = vi.spyOn(quasar.Notify, 'create'); // 安裝 Quasar 時 create 會被換掉，所以要在 open 之後才監看
    q('style-airy')!.click();
    await flushPromises();
    const onError = select.mock.calls[0]![1] as () => void;
    onError();
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'negative', message: zhTW.theme.saveFailed }));
  });

  it('Demo 站顯示只存在瀏覽器的提示', async () => {
    useThemeStore().applyMe({ ...FACTORY_ME_THEME, canSave: false });
    await open();
    expect(q('theme-demo-hint')?.textContent).toContain(zhTW.theme.demoLocalHint);
  });

  it('正式站沒有 Demo 提示', async () => {
    useThemeStore().applyMe(FACTORY_ME_THEME);
    await open();
    expect(q('theme-demo-hint')).toBeNull();
  });
});
