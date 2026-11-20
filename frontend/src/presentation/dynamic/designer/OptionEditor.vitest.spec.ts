import { flushPromises, mount } from '@vue/test-utils';
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerOption } from 'src/domain/dynamic/types';
import { setLocaleSettings } from 'src/i18n/locale-settings';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import OptionEditor from './OptionEditor.vue';

const options: DesignerOption[] = [{ id: 1, value: 'opt_1', label: { 'zh-TW': '甲' }, active: true } as DesignerOption];

function mountEditor() {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(OptionEditor, {
    props: { moduleKey: 'm', fieldId: 5, options },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
}
const buttons = () => [...document.body.querySelectorAll('button')];
const disabled = (el: Element) => el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true';

describe('OptionEditor 必填語系', () => {
  beforeEach(() => { setLocaleSettings({ requiredText: ['zh-TW', 'en-US'] }); });
  afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks(); setLocaleSettings({ requiredText: ['zh-TW'] }); });

  it('既有選項沒改標籤時，儲存不停用', async () => {
    const w = mountEditor();
    await flushPromises();
    const save = document.body.querySelector('button[title="儲存選項"]')!;
    expect(disabled(save)).toBe(false);
    w.unmount();
  });

  it('既有選項改了標籤但缺必填語系，儲存停用；補齊後可存', async () => {
    const w = mountEditor();
    await flushPromises();
    const inputs = w.findAllComponents({ name: 'I18nTextInput' });
    await inputs[0]!.vm.$emit('update:modelValue', { 'zh-TW': '乙' });
    await flushPromises();
    const save = document.body.querySelector('button[title="儲存選項"]')!;
    expect(disabled(save)).toBe(true);
    await inputs[0]!.vm.$emit('update:modelValue', { 'zh-TW': '乙', 'en-US': 'B' });
    await flushPromises();
    expect(disabled(save)).toBe(false);
    w.unmount();
  });

  it('新增選項照必填語系擋', async () => {
    const w = mountEditor();
    await flushPromises();
    const add = buttons().find((b) => b.textContent?.includes('新增選項'))!;
    expect(disabled(add)).toBe(true);
    const inputs = w.findAllComponents({ name: 'I18nTextInput' });
    await inputs[1]!.vm.$emit('update:modelValue', { 'zh-TW': '丙', 'en-US': 'C' });
    await flushPromises();
    expect(disabled(add)).toBe(false);
    w.unmount();
  });
});
