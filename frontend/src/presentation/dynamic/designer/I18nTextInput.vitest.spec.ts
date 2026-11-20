import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import { setLocaleSettings } from 'src/i18n/locale-settings';
import zhTW from 'src/i18n/zh-TW.json';
import I18nTextInput from './I18nTextInput.vue';

function mountInput(modelValue: Record<string, string>, extra: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(I18nTextInput, {
    props: { modelValue, label: '名稱', ...extra },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

const validate = (w: ReturnType<typeof mountInput>) => (w.vm as unknown as { validate(): Promise<boolean> }).validate();
afterEach(() => setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th', 'vi'], requiredText: ['zh-TW'] }));

describe('I18nTextInput', () => {
  it('I18nTextInput requires zh-TW', async () => {
    const w = mountInput({ 'en-US': 'Name' });
    expect(w.get('[data-testid="i18n-zh-TW"]').text()).toContain('*');
    expect(await (w.vm as unknown as { validate(): Promise<boolean> }).validate()).toBe(false);
    await w.setProps({ modelValue: { 'zh-TW': '名稱' } });
    expect(await (w.vm as unknown as { validate(): Promise<boolean> }).validate()).toBe(true);
  });

  it('keeps the other locales collapsed and drops emptied entries', async () => {
    const w = mountInput({ 'zh-TW': '名稱' });
    expect(w.get('.q-expansion-item').classes()).toContain('q-expansion-item--collapsed');
    await w.get('[data-testid="i18n-zh-TW"] input').setValue('');
    expect(w.emitted('update:modelValue')![0]).toEqual([{}]);
  });

  it('必填 zh-TW 與 en-US 時兩個必填框都有 *，展開區只列其餘啟用語系', () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US', 'th'], requiredText: ['zh-TW', 'en-US'] });
    const w = mountInput({});
    expect(w.get('[data-testid="i18n-zh-TW"]').text()).toContain('*');
    expect(w.get('[data-testid="i18n-en-US"]').text()).toContain('*');
    expect(w.find('.q-expansion-item [data-testid="i18n-th"]').exists()).toBe(true);
    expect(w.find('[data-testid="i18n-vi"]').exists()).toBe(false);
  });

  it('停用語系不顯示輸入框，但改別的語系時保留它的值', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'], requiredText: ['zh-TW'] });
    const w = mountInput({ 'zh-TW': '名稱', th: 'ชื่อ' });
    expect(w.find('[data-testid="i18n-th"]').exists()).toBe(false);
    await w.get('[data-testid="i18n-zh-TW"] input').setValue('新名稱');
    expect(w.emitted('update:modelValue')![0]).toEqual([{ 'zh-TW': '新名稱', th: 'ชื่อ' }]);
  });

  it('必填改成英文時，只有英文是必填框，繁中放展開區', async () => {
    setLocaleSettings({ enabled: ['zh-TW', 'en-US'], requiredText: ['en-US'] });
    const w = mountInput({ 'zh-TW': '名稱' });
    expect(w.get('[data-testid="i18n-en-US"]').text()).toContain('*');
    expect(await validate(w)).toBe(false);
    await w.setProps({ modelValue: { 'en-US': 'Name' } });
    expect(await validate(w)).toBe(true);
  });

  it('編輯沒改的既有值不擋，改了才擋並說缺哪個語系', async () => {
    setLocaleSettings({ requiredText: ['zh-TW', 'en-US'] });
    const original = { 'zh-TW': '名稱' };
    const w = mountInput({ 'zh-TW': '名稱' }, { original });
    expect(await validate(w)).toBe(true);
    await w.setProps({ modelValue: { 'zh-TW': '新名稱' } });
    expect(await validate(w)).toBe(false);
    expect(w.text()).toContain('en-US');
  });

  it('optional 時必填語系都不必填', async () => {
    setLocaleSettings({ requiredText: ['zh-TW', 'en-US'] });
    const w = mountInput({}, { optional: true });
    expect(w.get('[data-testid="i18n-zh-TW"]').text()).not.toContain('*');
    expect(await validate(w)).toBe(true);
  });
});
