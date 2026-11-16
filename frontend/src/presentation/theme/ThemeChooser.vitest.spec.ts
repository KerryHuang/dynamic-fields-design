import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import ThemeChooser from './ThemeChooser.vue';

function make(props: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(ThemeChooser, {
    props: { mode: 'dark', selectedStyle: 'airy', allowFollow: true, defaults: { mode: 'light', style: 'enterprise' }, ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}
const button = (w: ReturnType<typeof make>, text: string) => w.findAll('.q-btn').find((b) => b.text().includes(text));

describe('ThemeChooser', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('allowFollow 時有跟隨選項且顯示目前預設', () => {
    const w = make();
    expect(button(w, '跟隨全站預設（目前：淺色）')).toBeDefined();
    expect(w.find('[data-testid="style-follow"]').text()).toContain('跟隨全站預設（目前：正式企業）');
  });

  it('不 allowFollow 時沒有跟隨選項', () => {
    const w = make({ allowFollow: false });
    expect(w.text()).not.toContain('跟隨全站預設');
    expect(w.find('[data-testid="style-follow"]').exists()).toBe(false);
  });

  it('選跟隨會 emit null', async () => {
    const w = make();
    await button(w, '跟隨全站預設')!.trigger('click');
    expect(w.emitted('update:mode')).toEqual([[null]]);
    await w.find('[data-testid="style-follow"]').trigger('click');
    expect(w.emitted('update:style')).toEqual([[null]]);
  });

  it('選模式與風格會 emit 對應的值', async () => {
    const w = make();
    await button(w, '依系統')!.trigger('click');
    expect(w.emitted('update:mode')).toEqual([['system']]);
    await w.find('[data-testid="style-industrial"]').trigger('click');
    expect(w.emitted('update:style')).toEqual([['industrial']]);
  });

  it('八種風格各有名稱、說明與帶對應 data-style 的小樣本', () => {
    const w = make();
    for (const s of ['workbench', 'airy', 'enterprise', 'industrial', 'glass', 'bento', 'command', 'botanical'] as const) {
      const row = w.find(`[data-testid="style-${s}"]`);
      expect(row.text()).toContain(zhTW.theme.styles[s].name);
      expect(row.text()).toContain(zhTW.theme.styles[s].desc);
      const sample = row.find('.theme-sample');
      expect(sample.attributes('data-style')).toBe(s);
      expect(['light', 'dark']).toContain(sample.attributes('data-mode'));
    }
  });

  it('disabled 時不能操作', async () => {
    const w = make({ disabled: true });
    await button(w, '依系統')!.trigger('click');
    await w.find('[data-testid="style-airy"]').trigger('click');
    expect(w.emitted('update:mode')).toBeUndefined();
    expect(w.emitted('update:style')).toBeUndefined();
  });
});
