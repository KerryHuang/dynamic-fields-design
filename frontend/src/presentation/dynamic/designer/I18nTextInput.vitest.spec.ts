import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import I18nTextInput from './I18nTextInput.vue';

function mountInput(modelValue: Record<string, string>) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(I18nTextInput, {
    props: { modelValue, label: '名稱' },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

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
});
